using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>What one re-evaluation pass changed, so the caller can log it and ring the doorbell.</summary>
/// <param name="Parked">Tasks newly held for a human, or held for a reason that has since changed.</param>
/// <param name="Recovered">
/// Tasks whose dead-blocker hold was lifted because the blocker is back in the pipeline
/// (Decisions Log #61) — still Blocked, but waiting ordinarily rather than crying wolf.
/// </param>
/// <remarks>
/// These are tasks, not observations: a task appears at most once in each list however many
/// of its blockers changed in the pass, so the caller's one-line-per-task log says one thing
/// once. A task with one blocker newly dead and another recovered appears in both, which is
/// what actually happened to it.
/// </remarks>
public sealed record DependencyReevaluation(
    IReadOnlyList<Guid> Unblocked, IReadOnlyList<Guid> Parked, IReadOnlyList<Guid> Recovered)
{
    public static readonly DependencyReevaluation Nothing = new([], [], []);

    public bool ChangedAnything => Unblocked.Count > 0 || Parked.Count > 0 || Recovered.Count > 0;
}

/// <summary>
/// Re-evaluates blocked tasks against the dependencies they wait on (Decisions Log #34).
/// Two doors, one routine: the closeout monitor calls <see cref="ForDependencyAsync"/> the
/// moment it appends RunCompleted, so whichever node observed the merge unblocks the
/// dependents; the dispatch loop calls <see cref="ForEveryBlockedTaskAsync"/> each cycle as
/// the safety net that also catches blockers which died rather than finished. That mirrors
/// the platform's standing shape — NOTIFY is a doorbell, polling is what makes it correct.
/// This is the one owner of dependency state transitions, which is why the recovery of a dead
/// blocker is appended here too (Decisions Log #61): the same pass that recorded a hold is the
/// pass that observes the hold has stopped being true, so the two can never disagree.
/// </summary>
public static class TaskDependencyResolver
{
    /// <summary>Every Blocked task waiting on <paramref name="dependencyId"/>, re-evaluated now.</summary>
    public static async Task<DependencyReevaluation> ForDependencyAsync(
        IDocumentSession session, Guid dependencyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> dependents = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("d.data ->> 'state' = ?", TaskState.Blocked.Value))
            .Where(task => task.UnmetDependencies.Contains(dependencyId))
            .ToListAsync(cancellationToken);

        return await ReevaluateAsync(session, dependents, now, cancellationToken);
    }

    /// <summary>
    /// Every Blocked task, re-evaluated. The set is small by construction (a blocked task is
    /// waiting, not working), and this is the only path that notices a blocker which can no
    /// longer close out — Failed, Abandoned, or Done on a run that ended without an observed
    /// merge. None of those produce a closeout event for <see cref="ForDependencyAsync"/> to
    /// react to, so only the sweep sees them. It is also the only path that notices the
    /// reverse: a blocker put back to work by h9k task retry appends nothing to its dependents
    /// either, so one dispatch cycle later this pass lifts the hold it recorded.
    /// </summary>
    public static async Task<DependencyReevaluation> ForEveryBlockedTaskAsync(
        IDocumentSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> blocked = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("d.data ->> 'state' = ?", TaskState.Blocked.Value))
            .ToListAsync(cancellationToken);

        return await ReevaluateAsync(session, blocked, now, cancellationToken);
    }

    /// <summary>
    /// Appends without an expected version on purpose, because both races it loses replay to
    /// the right answer. Two nodes observing the same closeout at once write the same
    /// TaskDependencyCompleted twice, and a duplicate replays to the same state — the
    /// dependency is already off the unmet set. A human command landing between the read and
    /// the save (unassign, abandon) moves the task out of Blocked, and every dependency Apply
    /// is guarded on being Blocked, so the late append replays as a no-op instead of smearing
    /// dependency state onto a lifecycle that has moved on. Optimistic concurrency here would
    /// buy a tidier stream at the cost of a retry loop on races whose outcomes are already
    /// correct.
    /// </summary>
    private static async Task<DependencyReevaluation> ReevaluateAsync(
        IDocumentSession session,
        IReadOnlyList<TaskListItem> blocked,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (blocked.Count == 0)
        {
            return DependencyReevaluation.Nothing;
        }

        List<Guid> unblocked = [];
        List<Guid> parked = [];
        List<Guid> recovered = [];

        foreach (TaskListItem candidate in blocked)
        {
            TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                candidate.Id, token: cancellationToken);
            if (task is null || task.State != TaskState.Blocked)
            {
                continue;
            }

            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
                session, task.UnmetDependencies, cancellationToken);

            bool held = false;
            bool lifted = false;

            // Each decision is applied to the in-memory aggregate as it is appended, so a pass
            // that clears two blockers records the second one's remaining set correctly rather
            // than twice describing the world as it was before the first.
            foreach (TaskDependency dependency in dependencies)
            {
                if (!dependency.Blocks)
                {
                    TaskDependencyCompleted completed = TaskDecider.DependencyCompleted(task, dependency.Id, now);
                    session.Events.Append(task.Id, completed);
                    task.Apply(completed);
                    continue;
                }

                // Dead NOW is the whole question, asked fresh every pass against the blocker's
                // observed state. A blocker back in the pipeline is not dead, however it got
                // there, so nothing here has to know that h9k task retry exists.
                string? recordedFailure = task.RecordedDependencyFailure(dependency.Id);
                if (dependency.IsDead)
                {
                    string reason = DeathReason(task, dependency);
                    if (recordedFailure == reason)
                    {
                        continue;
                    }

                    TaskDependencyFailed died = TaskDecider.DependencyFailed(task, dependency.Id, reason, now);
                    session.Events.Append(task.Id, died);
                    task.Apply(died);
                    held = true;
                    continue;
                }

                if (recordedFailure is not null)
                {
                    TaskDependencyRecovered recovery = TaskDecider.DependencyRecovered(
                        task,
                        dependency.Id,
                        $"Dependency {dependency.Describe()} is back in the pipeline and can reach "
                        + "true closeout again, so the dead-blocker hold no longer describes anything.",
                        now);
                    session.Events.Append(task.Id, recovery);
                    task.Apply(recovery);
                    lifted = true;
                }
            }

            // One entry per task, decided after its whole dependency set has been walked: two
            // blockers changing on the same task is still one thing to tell the human about.
            if (held)
            {
                parked.Add(task.Id);
            }

            if (lifted)
            {
                recovered.Add(task.Id);
            }

            if (task.State == TaskState.Queued)
            {
                unblocked.Add(task.Id);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new DependencyReevaluation(unblocked, parked, recovered);
    }

    /// <summary>
    /// Why this blocker will never close out, plus the remedy on the dependent's own side.
    /// Recomputed every pass so it can be compared with what the task already records: a
    /// blocker whose death changed shape (Failed, then resolved by a human) gets a fresh
    /// record rather than keeping advice the decider would now refuse.
    /// </summary>
    private static string DeathReason(TaskAggregate task, TaskDependency dependency) =>
        $"{dependency.DescribeDeath()} (h9k task unassign {task.Id}, then h9k task draft {task.Id}).";

}
