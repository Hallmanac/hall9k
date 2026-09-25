using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// The standing pass that keeps one GitHub review request at exactly one live pr-review task
/// across every node of the owner's fleet.
/// <para>
/// Root cause it answers: two daemons of one owner, both with the project registered and auto
/// pr-review on under the same GitHub login, each mint a task for the same review request within
/// seconds. Each node keeps its own <see cref="ObservedReviewRequest"/> record (its id is prefixed
/// with the node's own id), and the store-level dedup in <c>AutoPrReviewEngine.CreateOneAsync</c>
/// sees the peer's task only once replication lands, about two minutes later, after both
/// dispatchers have claimed. In the incident (arx-platform 2140, 2143 and 2147) the loser's twin
/// was queued on one node under a per-project ceiling of 0 and was claimed on the other after
/// replication, and the two nodes ran different auto pr-review modes because
/// <c>ProjectSettingsChanged</c> is node-scoped.
/// </para>
/// <para>
/// The decision is deliberately not made in the replication inbox, which is a bare replay with no
/// side effects, and a replicated <c>TaskClaimed</c> from the origin node would flip an abandoned
/// twin back to Claimed because the aggregate applies it unconditionally. So this pass runs every
/// auto-pr-review sweep and again right after a replication read that applied events, and it
/// re-evaluates from the store each time: a lifecycle event that replicates in after the abandon
/// is re-converged rather than resurrecting the duplicate. The dispatcher's claim-time refusal is
/// what stops a younger twin that is still queued once the older task is visible from ever starting a run; a twin already claimed before replication landed is stopped by this pass's abandon, which the abandoned-task check acts on at the run's next phase boundary.
/// </para>
/// </summary>
public sealed class PullRequestReviewDuplicateConvergence(
    IDocumentStore store,
    NodeContext node,
    ILogger<PullRequestReviewDuplicateConvergence> logger)
{
    private static readonly string[] TerminalStates =
        [TaskState.Done.Value, TaskState.Abandoned.Value];

    // The sweep and the post-replication call can overlap in one process; serialising them keeps
    // two passes from racing to abandon the same twin (the fenced append would catch it anyway,
    // this only spares the noise).
    private readonly SemaphoreSlim _pass = new(1, 1);

    /// <summary>Returns how many duplicates this pass abandoned.</summary>
    public async Task<int> ConvergeAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken);
        try
        {
            await using IQuerySession query = store.QuerySession();
            string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
                query, node.OwnerId, cancellationToken);

            IReadOnlyList<TaskListItem> live = await ReadLiveAutoCreatedAsync(query, reference: null, cancellationToken);

            int abandoned = 0;
            foreach (IGrouping<string, TaskListItem> onePullRequest in live
                .Where(task => task.ExternalReference is not null
                    && PullRequestReviewDuplicateRule.IsRival(task, node.OwnerId, ownerRootFingerprint))
                .GroupBy(task => task.ExternalReference!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                Guid survivorId = PullRequestReviewDuplicateRule.SurvivorOf(onePullRequest.Select(task => task.Id));
                foreach (TaskListItem twin in onePullRequest.Where(task => task.Id != survivorId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await TryAbandonAsync(twin.Id, survivorId, cancellationToken))
                    {
                        abandoned++;
                    }
                }
            }

            await ReleaseAbandonedTasksHeldHereAsync(query, cancellationToken);
            return abandoned;
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>
    /// Every live auto-created pr-review task, or only those on <paramref name="reference"/> (any
    /// casing, the same way the sweep's own already-covered fast path matches) when one is given.
    /// The one query the pass and the dispatcher's claim-time refusal both read through, so neither
    /// can disagree with the other about what counts as live.
    /// </summary>
    internal static async Task<IReadOnlyList<TaskListItem>> ReadLiveAutoCreatedAsync(
        IQuerySession query, string? reference, CancellationToken cancellationToken)
    {
        IQueryable<TaskListItem> live = query.Query<TaskListItem>()
            .Where(task => task.WasAutoPrReviewCreated)
            .Where(task => task.MatchesSql(
                "d.data ->> 'type' = ? AND d.data ->> 'state' NOT IN (?, ?)",
                TaskType.PrReview.Value, TerminalStates[0], TerminalStates[1]));
        if (reference is not null)
        {
            live = live.Where(task => task.MatchesSql("lower(d.data ->> 'externalReference') = lower(?)", reference));
        }

        return await live.ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Abandons one younger twin as the owner, hands its mentions to the survivor, and gives back
    /// whatever of it this node holds, all in one transaction fenced on both streams. A lost race
    /// is not a defect: the next pass reads the store again.
    /// </summary>
    private async Task<bool> TryAbandonAsync(Guid twinId, Guid survivorId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? twinFence = await session.Events.FetchStreamStateAsync(twinId, cancellationToken);
        StreamState? survivorFence = await session.Events.FetchStreamStateAsync(survivorId, cancellationToken);
        if (twinFence is null || survivorFence is null)
        {
            return false;
        }

        TaskAggregate? twin = await session.Events.AggregateStreamAsync<TaskAggregate>(
            twinId, version: twinFence.Version, token: cancellationToken);
        TaskAggregate? survivor = await session.Events.AggregateStreamAsync<TaskAggregate>(
            survivorId, version: survivorFence.Version, token: cancellationToken);
        if (twin is null || survivor is null || twin.State.IsTerminal || survivor.State.IsTerminal)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool holdsLedgerHolder = twin.HolderNodeId == node.NodeId;
        object[] twinEvents = holdsLedgerHolder
            ? [
                TaskDecider.Abandon(twin, PullRequestReviewDuplicateRule.AbandonReason(survivorId), now, node.OwnerId),
                TaskDecider.ReleaseHolder(twin, now),
            ]
            : [TaskDecider.Abandon(twin, PullRequestReviewDuplicateRule.AbandonReason(survivorId), now, node.OwnerId)];
        session.Events.Append(twinId, expectedVersion: twinFence.Version + twinEvents.Length, twinEvents);

        IReadOnlyList<PullRequestReviewMentionObserved> carried = await MentionsToCarryAsync(
            session, twinId, survivor, cancellationToken);
        if (carried.Count > 0)
        {
            object[] mentionEvents =
            [
                .. carried.Select(mention => (object)TaskDecider.ObservePrReviewMention(
                    survivor, mention.PullRequestUrl, mention.CommentId, mention.CommentAuthorLogin,
                    mention.CommentBody, mention.CommentUrl, mention.CommentCreatedAt, mention.ObservedAt,
                    mention.CommentDatabaseId)),
            ];
            session.Events.Append(survivorId, expectedVersion: survivorFence.Version + mentionEvents.Length, mentionEvents);
        }

        StageReleaseOfWhatThisNodeHolds(session, twin, holdsLedgerHolder, now);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug(
                "Task {TaskId} or its surviving twin {SurvivorId} moved while the duplicate pass was deciding; "
                + "the next pass reads them again", twinId, survivorId);
            return false;
        }

        logger.LogInformation(
            "Task {TaskId} abandoned as a duplicate: task {SurvivorId} already holds this review request "
            + "(the smaller id survives){Mentions}",
            twinId, survivorId, carried.Count > 0 ? $"; {carried.Count} mention(s) carried over" : string.Empty);
        return true;
    }

    /// <summary>
    /// The mentions recorded on the twin that the survivor does not already carry, oldest first so
    /// the survivor's own latest-mention fields end on the newest one. A mention older than what
    /// the survivor already shows as its latest is left on the abandoned twin's stream: the
    /// aggregate keeps only the latest, and moving it backwards would misreport who tagged the
    /// owner most recently.
    /// </summary>
    private static async Task<IReadOnlyList<PullRequestReviewMentionObserved>> MentionsToCarryAsync(
        IDocumentSession session, Guid twinId, TaskAggregate survivor, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> twinStream = await session.Events.FetchStreamAsync(twinId, token: cancellationToken);
        IReadOnlyList<IEvent> survivorStream = await session.Events.FetchStreamAsync(survivor.Id, token: cancellationToken);
        HashSet<string> alreadyOnSurvivor =
        [
            .. survivorStream.Select(recorded => recorded.Data)
                .OfType<PullRequestReviewMentionObserved>()
                .Select(mention => mention.CommentId),
        ];

        return
        [
            .. twinStream.Select(recorded => recorded.Data)
                .OfType<PullRequestReviewMentionObserved>()
                .Where(mention => !alreadyOnSurvivor.Contains(mention.CommentId))
                .Where(mention => survivor.LatestMentionCreatedAt is not { } latest || mention.CommentCreatedAt > latest)
                .OrderBy(mention => mention.CommentCreatedAt),
        ];
    }

    /// <summary>
    /// Deletes this node's own <see cref="TaskLease"/> for the twin (another node's lease row is
    /// never this node's to delete) and, when this node is the ledger holder, records the release
    /// the dispatcher's own pending-release sweep completes against the ledger. A lease left in
    /// place would be refreshed by the heartbeat service for as long as the twin's run lives, and
    /// the expiry sweep skips a task that is not Claimed, so nothing else would ever remove it.
    /// </summary>
    private void StageReleaseOfWhatThisNodeHolds(
        IDocumentSession session, TaskAggregate twin, bool holdsLedgerHolder, DateTimeOffset now)
    {
        session.DeleteWhere<TaskLease>(lease => lease.Id == twin.Id && lease.NodeId == node.NodeId);
        if (!holdsLedgerHolder)
        {
            return;
        }

        session.Store(new TaskHolderReleasePending
        {
            Id = TaskHolderReleasePending.KeyFor(twin.Id, node.NodeId),
            TaskId = twin.Id,
            ProjectId = twin.ProjectId,
            NodeId = node.NodeId,
            RecordedAt = now,
            LastFailureReason = "owed by the duplicate review convergence pass; not yet confirmed against the ledger",
        });
    }

    /// <summary>
    /// An auto-created review already Abandoned that this node still holds something of, whether
    /// this node abandoned it or the abandon arrived by replication: a <see cref="TaskLease"/> row,
    /// the ledger holder, or both. The run behind it, if any, is retired by the abandoned-task fence
    /// at its next phase boundary, but the lease and the ledger holder it earned are this node's to
    /// give back now. The two are looked for separately because a finished review run deletes its
    /// own lease and keeps the holder (only <see cref="TaskHolderReleased"/> clears it, and the
    /// follow-through and closeout engines act only on live tasks), so an abandon that replicates in
    /// afterward finds no lease to lead the scan to the holder.
    /// </summary>
    private async Task ReleaseAbandonedTasksHeldHereAsync(IQuerySession query, CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        IReadOnlyList<Guid> leasedHere = await query.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .Select(lease => lease.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> claimedHere = await query.Query<TaskListItem>()
            .Where(task => task.WasAutoPrReviewCreated && task.ClaimedByNodeId == nodeId)
            .Where(task => task.MatchesSql(
                "d.data ->> 'type' = ? AND d.data ->> 'state' = ?",
                TaskType.PrReview.Value, TaskState.Abandoned.Value))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        foreach (Guid taskId in leasedHere.Union(claimedHere))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskListItem? item = await query.LoadAsync<TaskListItem>(taskId, cancellationToken);
            if (item is not { WasAutoPrReviewCreated: true } || item.Type != TaskType.PrReview
                || item.State != TaskState.Abandoned)
            {
                continue;
            }

            await using IDocumentSession session = store.LightweightSession();
            StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
            TaskAggregate? task = fence is null
                ? null
                : await session.Events.AggregateStreamAsync<TaskAggregate>(
                    taskId, version: fence.Version, token: cancellationToken);
            if (fence is null || task is null || task.State != TaskState.Abandoned)
            {
                continue;
            }

            bool holdsLedgerHolder = task.HolderNodeId == node.NodeId;
            if (!holdsLedgerHolder && !leasedHere.Contains(taskId))
            {
                continue;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (holdsLedgerHolder)
            {
                session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseHolder(task, now));
            }

            StageReleaseOfWhatThisNodeHolds(session, task, holdsLedgerHolder, now);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Abandoned review task {TaskId} still held a lease or ledger holder on this node; released it", taskId);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                logger.LogDebug("Task {TaskId} moved while its holder was being released; the next pass retries", taskId);
            }
        }
    }
}
