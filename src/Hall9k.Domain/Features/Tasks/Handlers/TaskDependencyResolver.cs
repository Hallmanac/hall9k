using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>What one re-evaluation pass changed, so the caller can log it and ring the doorbell.</summary>
public sealed record DependencyReevaluation(IReadOnlyList<Guid> Unblocked, IReadOnlyList<Guid> Parked)
{
    public static readonly DependencyReevaluation Nothing = new([], []);

    public bool ChangedAnything => Unblocked.Count > 0 || Parked.Count > 0;
}

/// <summary>
/// Re-evaluates blocked tasks against the dependencies they wait on (Decisions Log #34).
/// Two doors, one routine: the closeout monitor calls <see cref="ForDependencyAsync"/> the
/// moment it appends RunCompleted, so whichever node observed the merge unblocks the
/// dependents; the dispatch loop calls <see cref="ForEveryBlockedTaskAsync"/> each cycle as
/// the safety net that also catches blockers which died rather than finished. That mirrors
/// the platform's standing shape — NOTIFY is a doorbell, polling is what makes it correct.
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
    /// react to, so only the sweep sees them.
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

                if (dependency.IsDead && !TaskDecider.HasRecordedDependencyFailure(task, dependency.Id))
                {
                    TaskDependencyFailed died = TaskDecider.DependencyFailed(
                        task,
                        dependency.Id,
                        $"{dependency.DescribeDeath()} "
                        + $"(h9k task unassign {task.Id}, then h9k task draft {task.Id}).",
                        now);
                    session.Events.Append(task.Id, died);
                    task.Apply(died);
                    parked.Add(task.Id);
                }
            }

            if (task.State == TaskState.Queued)
            {
                unblocked.Add(task.Id);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new DependencyReevaluation(unblocked, parked);
    }
}
