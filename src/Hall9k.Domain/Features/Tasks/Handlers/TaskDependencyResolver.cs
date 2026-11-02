using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using JasperFx.Events;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// One task held for a human this pass, with the reason it was held for — the same sentence
/// h9k task show puts in front of the human, so the operator log and the task surface can
/// never describe the same hold differently.
/// </summary>
public sealed record DependencyHold(Guid TaskId, string Reason);

/// <summary>
/// One task whose dead-blocker hold was lifted this pass, with whatever hold the task is left
/// carrying afterwards (Decisions Log #61). A dependent waiting on two dead blockers, one of
/// which the human retried, is not free: the other blocker's reason survives, h9k status still
/// reads it as NeedsHuman, and a caller that announced the recovery on its own would tell the
/// operator the opposite of what the task surface says. Null is the ordinary case — nothing
/// dead is left, and the task waits the ordinary way again.
/// </summary>
public sealed record DependencyRecovery(Guid TaskId, string? SurvivingReason);

/// <summary>What one re-evaluation pass changed, so the caller can log it and ring the doorbell.</summary>
/// <param name="Parked">Tasks newly held for a human, or held for a reason that has since changed.</param>
/// <param name="Recovered">
/// Tasks that had a dead-blocker hold lifted because that blocker is back in the pipeline
/// (Decisions Log #61) — still Blocked, and waiting ordinarily rather than crying wolf unless
/// <see cref="DependencyRecovery.SurvivingReason"/> says another blocker is still dead.
/// </param>
/// <remarks>
/// These are tasks, not observations: a task appears at most once in each list however many
/// of its blockers changed in the pass, so the caller's one-line-per-task log says one thing
/// once. A task with one blocker newly dead and another recovered appears in both, which is
/// what actually happened to it.
/// </remarks>
public sealed record DependencyReevaluation(
    IReadOnlyList<Guid> Unblocked,
    IReadOnlyList<DependencyHold> Parked,
    IReadOnlyList<DependencyRecovery> Recovered)
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
        List<DependencyHold> parked = [];
        List<DependencyRecovery> recovered = [];

        foreach (TaskListItem candidate in blocked)
        {
            TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                candidate.Id, token: cancellationToken);
            if (task is null || task.State != TaskState.Blocked)
            {
                continue;
            }

            (IReadOnlyList<TaskDependency> dependencies, IReadOnlyList<Guid> missingDependencyIds) =
                await TaskDependencyQuery.LoadWithMissingAsync(session, task.UnmetDependencies, cancellationToken);

            // A purge can hard-delete this candidate's own stream between the aggregate read
            // above and the appends below; Marten's unfenced Append silently starts a brand-new
            // stream rather than failing when the target is gone, which would leave a stray
            // post-purge event and a phantom projection document behind (Copilot review, PR
            // #338). Re-checked immediately before appending anything for this candidate,
            // narrowing the window as far as a per-candidate check reasonably can without turning
            // this batched sweep into a per-append transaction.
            if (await session.Events.FetchStreamStateAsync(candidate.Id, cancellationToken) is null)
            {
                continue;
            }

            // The newest death this pass recorded, which is the one the task surface will show:
            // a recovery later in the same walk only removes blockers that are alive now, so it
            // can never displace a death recorded a moment ago.
            string? held = null;
            bool lifted = false;

            // A dependency id naming no TaskListItem on this node is failed only when this node's own
            // purge record names it (PurgedTaskRecord): the task was destroyed here, so it can never
            // reach closeout or Delivered, and the dependent gets the same dead hold a Failed or
            // Abandoned blocker gets, with its own remedy on the dependent's side. Any other missing
            // id proves nothing. The edge is a bare id that records no project, and a purge deletes
            // the project's own record, so an unseen project (not registered or joined here, or its
            // events not replicated yet) looks exactly like a purged one. TaskDependencyFailed is
            // project-scoped, so recording it from a partial view would park the task for every node,
            // and a node that does see the blocker would lift it again next cycle. Those ids record
            // nothing and the dependent stays Blocked; the surfaces name them as unknown blockers.
            IReadOnlyDictionary<Guid, PurgedTaskRecord> purgedHere =
                await PurgedHereAsync(session, missingDependencyIds, cancellationToken);
            IReadOnlyList<IEvent>? streamEvents = null;
            foreach (PurgedTaskRecord purge in missingDependencyIds
                .Select(id => purgedHere.GetValueOrDefault(id))
                .OfType<PurgedTaskRecord>())
            {
                Guid missingId = purge.Id;
                string reason = MissingDependencyReason(task, missingId);
                if (task.RecordedDependencyFailure(missingId) == reason)
                {
                    continue;
                }

                // Said once per dependent and purged blocker, ever. The purge record never leaves
                // this node, but the failure it produced does: a node that still has the blocker
                // lifts it with a TaskDependencyRecovered, which clears the recorded failure here
                // too, and this node's record keeps naming the blocker, so recording again would
                // trade failure and recovery with that node on every sweep. The stream remembers
                // what the aggregate forgot. Only a failure this node appended itself counts: the
                // reason text is the same on every node for a given dependent and blocker, so a
                // replicated copy (it carries the origin node header, which a local append never
                // does) is another node's purge, not proof that this node's own purge was said.
                // Counting it would leave every node believing another had already spoken, and the
                // dependent Blocked with the dead hold recorded by none of them. Two more kinds of
                // past failure do not count either: one that replayed as a no-op because the task was
                // not Blocked when it landed (a claim committed between a sweep's read and its
                // unfenced append), which a later release leaves unrecorded, and one observed before
                // this node's purge, which an earlier build's unconditional sweep wrote and which
                // describes no purge at all. A sweep that began before the purge committed still
                // reads the purge record before it appends, so its failure is stamped no earlier
                // than the purge (below) and is never mistaken for one of those.
                streamEvents ??= await session.Events.FetchStreamAsync(task.Id, token: cancellationToken);
                if (await AlreadySaidAsync(session, task.Id, streamEvents, purge, reason, cancellationToken))
                {
                    continue;
                }

                // The sweep's clock was read before it started, so a purge that committed meanwhile
                // can carry a later PurgedAt than this sweep's `now`. The purge record is read here,
                // so this failure was observed after the purge whatever `now` says.
                DateTimeOffset observedAt = now < purge.PurgedAt ? purge.PurgedAt : now;
                TaskDependencyFailed died = TaskDecider.DependencyFailed(task, missingId, reason, observedAt);
                session.Events.Append(task.Id, died);
                task.Apply(died);
                held = reason;
            }

            // Each decision is applied to the in-memory aggregate as it is appended, so a pass
            // that clears two blockers records the second one's remaining set correctly rather
            // than twice describing the world as it was before the first.
            foreach (TaskDependency dependency in dependencies)
            {
                // Asked per edge through StackedEdgeRules, the same rule TaskDecider.Assign froze
                // the unmet set with: the blocker this task declared itself stacked on is met at
                // the parent's Delivered, every other one at true closeout (Decisions Log #34,
                // unchanged for a plain edge).
                if (!StackedEdgeRules.Blocks(task, dependency))
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
                if (StackedEdgeRules.IsDead(task, dependency))
                {
                    string reason = DeathReason(task, dependency);
                    if (RecordsDeath(recordedFailure, task, dependency))
                    {
                        continue;
                    }

                    TaskDependencyFailed died = TaskDecider.DependencyFailed(task, dependency.Id, reason, now);
                    session.Events.Append(task.Id, died);
                    task.Apply(died);
                    held = reason;
                    continue;
                }

                if (recordedFailure is not null)
                {
                    TaskDependencyRecovered recovery = TaskDecider.DependencyRecovered(
                        task, dependency.Id, RecoveryReason(task, dependency), now);
                    session.Events.Append(task.Id, recovery);
                    task.Apply(recovery);
                    lifted = true;
                }
            }

            // One entry per task, decided after its whole dependency set has been walked: two
            // blockers changing on the same task is still one thing to tell the human about.
            if (held is not null)
            {
                parked.Add(new DependencyHold(task.Id, held));
            }

            // Read off the aggregate after the whole walk rather than off the recovery itself:
            // what a lifted hold leaves behind is whatever the task's other blockers still say,
            // and a blocker that is dead and unchanged appends nothing this pass to say it.
            if (lifted)
            {
                recovered.Add(new DependencyRecovery(task.Id, task.DependencyFailureReason));
            }

            if (task.State == TaskState.Queued)
            {
                unblocked.Add(task.Id);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new DependencyReevaluation(unblocked, parked, recovered);
    }

    /// <summary>The purge records for the ids among <paramref name="ids"/> that this node's own purge destroyed.</summary>
    private static async Task<IReadOnlyDictionary<Guid, PurgedTaskRecord>> PurgedHereAsync(
        IDocumentSession session, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, PurgedTaskRecord>();
        }

        IReadOnlyList<PurgedTaskRecord> records = await session.LoadManyAsync<PurgedTaskRecord>(
            cancellationToken, [.. ids]);
        return records.ToDictionary(record => record.Id);
    }

    /// <summary>
    /// Whether the dependent's own stream already holds a failure for this purge that this node
    /// recorded and that took effect: appended locally, observed at or after the purge, and
    /// actually recorded by the aggregate at that point (it replays to the reason where the
    /// version before it did not), and not since ended by a recovery this node recorded itself.
    /// </summary>
    private static async Task<bool> AlreadySaidAsync(
        IDocumentSession session,
        Guid taskId,
        IReadOnlyList<IEvent> streamEvents,
        PurgedTaskRecord purge,
        string reason,
        CancellationToken cancellationToken)
    {
        bool recordedHere = false;
        foreach (IEvent candidate in streamEvents)
        {
            // A recovery this node recorded itself (the blocker was alive here again) ends whatever
            // an earlier failure said: the blocker has since been seen, so a purge that destroys it
            // again is a new thing to say even when the purge record still carries its old date.
            // A recovery that replicated in from another node does not, which is the case the
            // once-per-dependent rule exists for.
            if (candidate.Data is TaskDependencyRecovered recovery
                && recovery.DependencyId == purge.Id
                && candidate.GetHeader(ReplicationEventHeaders.OriginNodeId) is null)
            {
                recordedHere = false;
                continue;
            }

            if (candidate.Data is not TaskDependencyFailed past
                || past.DependencyId != purge.Id
                || past.Reason != reason
                || past.ObservedAt < purge.PurgedAt
                || candidate.GetHeader(ReplicationEventHeaders.OriginNodeId) is not null)
            {
                continue;
            }

            TaskAggregate? before = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: candidate.Version - 1, token: cancellationToken);
            TaskAggregate? after = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: candidate.Version, token: cancellationToken);
            if (before?.RecordedDependencyFailure(purge.Id) != reason && after?.RecordedDependencyFailure(purge.Id) == reason)
            {
                recordedHere = true;
            }
        }

        return recordedHere;
    }

    /// <summary>
    /// Why this blocker will never close out, plus the remedy on the dependent's own side.
    /// Recomputed every pass so it can be compared with what the task already records: a
    /// blocker whose death changed shape (Failed, then resolved by a human) gets a fresh
    /// record rather than keeping advice the decider would now refuse.
    /// </summary>
    /// <remarks>
    /// The sentence is recorded on the dependent's own stream, which replicates through its project's ledger, so a
    /// blocker in another project is named by short id and state alone and never by its objective.
    /// </remarks>
    public static string DeathReason(TaskAggregate task, TaskDependency dependency) =>
        $"{dependency.DescribeDeath(task.ProjectId)} {DeathRemedy(task)}";

    /// <summary>
    /// Whether <paramref name="recordedFailure"/>, what the task already records about this blocker, is the death the
    /// sweep would record now: the current wording, or the older one that quoted the objective (see
    /// <see cref="LegacyDeathReason"/>).
    /// </summary>
    public static bool RecordsDeath(string? recordedFailure, TaskAggregate task, TaskDependency dependency) =>
        recordedFailure is not null
        && (recordedFailure == DeathReason(task, dependency)
            || recordedFailure == LegacyDeathReason(task, dependency));

    /// <summary>
    /// The sentence <see cref="DeathReason"/> read before a blocker in another project was named without its
    /// objective, which quotes the objective whatever project the blocker is in. A node that has not taken the change
    /// still records this form, and the ledger replicates it, so the sweep treats a recorded death in this form as the
    /// same observation: comparing only against the current wording would have two nodes on different versions
    /// overwrite each other's record on every pass for as long as the versions differ.
    /// </summary>
    private static string LegacyDeathReason(TaskAggregate task, TaskDependency dependency) =>
        $"{dependency.DescribeDeath()} {DeathRemedy(task)}";

    private static string DeathRemedy(TaskAggregate task) =>
        $"(h9k task dequeue {task.Id}, then h9k task draft {task.Id}).";

    /// <summary>Why a dead-blocker hold was lifted, naming the blocker the way <see cref="DeathReason"/> does.</summary>
    public static string RecoveryReason(TaskAggregate task, TaskDependency dependency) =>
        $"Dependency {dependency.DescribeFor(task.ProjectId)} is back in the pipeline and can reach "
        + "true closeout again, so the dead-blocker hold no longer describes anything.";

    /// <summary>
    /// Why a blocker id that names no task at all will never close out — there is no
    /// <see cref="TaskDependency"/> snapshot to describe, so this names only the id
    /// (<see cref="TaskDependencyGraph.DescribeCycle"/>'s own short-id convention) and the one
    /// remedy left: nothing on the blocker's own side can ever put it back under watch, because
    /// there is no blocker left.
    /// </summary>
    private static string MissingDependencyReason(TaskAggregate task, Guid dependencyId) =>
        $"Dependency {dependencyId.ToString("N")[^8..]} no longer exists — most likely its project was "
        + "purged — so it will never close out. Revise this task's dependencies "
        + $"(h9k task dequeue {task.Id}, then h9k task draft {task.Id}).";
}
