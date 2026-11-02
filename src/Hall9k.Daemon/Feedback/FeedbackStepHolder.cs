using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Documents;
using Marten;

namespace Hall9k.Daemon.Feedback;

/// <summary>What asking the ledger for a task's holder came to, for the node that wants to begin its feedback step.</summary>
internal enum FeedbackStepHolderVerdict
{
    /// <summary>This node is the holder now (or the task has no ledger record, so there is nothing to guard): the step may begin here.</summary>
    Held,

    /// <summary>The record names another node, so that node's step is the one: this node stands down.</summary>
    HeldByOther,

    /// <summary>The ledger could not be read or written, so the step waits and the next sweep asks again.</summary>
    Failed,
}

/// <summary>
/// The cross-node half of beginning a feedback step. Each node keeps its own Postgres, so the stream version that fences two
/// daemons of one database cannot stop two nodes of one owner from both beginning the step on a task with no placement. What
/// decides between nodes is the task's ledger record holder, the same conditional write the dispatcher's claim goes through
/// (<see cref="TaskLedgerHolder"/>): a node writes itself in before it appends the begun event, and a node that finds another
/// node named stands down. The holder is given back when the step ends, so the dispatcher's own claim of the released task finds
/// it free, unless the dispatcher on this node has already claimed the released task, in which case the holder is that run's
/// and is left alone. A holder write that cannot complete holds the step rather than letting it begin, the fail-closed rule every holder
/// write follows.
/// </summary>
internal sealed class FeedbackStepHolder(IDocumentStore store, NodeContext node, ILedger ledger, ILogger logger)
{
    public async Task<FeedbackStepHolderVerdict> TryHoldAsync(
        IDocumentSession session, Guid taskId, ProjectDetails project, string? ownerRootFingerprint, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            LedgerFile existing = await ledger.ReadAsync(
                project.RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cancellationToken);
            if (existing.FetchFailed)
            {
                logger.LogWarning(
                    "Task {TaskId}: its feedback step waits: the ledger record could not be read to check for a holder, because the fetch failed",
                    taskId);
                return FeedbackStepHolderVerdict.Failed;
            }

            if (!existing.Exists)
            {
                return FeedbackStepHolderVerdict.Held;
            }

            (LedgerCommitter committer, LedgerSigningKey signingKey, string fingerprint) =
                await DispatchEngine.ResolveLedgerIdentityAsync(session, node, ownerRootFingerprint, cancellationToken);
            string nodeName = (await session.LoadAsync<NodeDetails>(node.NodeId, cancellationToken))?.MachineName
                ?? Environment.MachineName;
            HolderClaimResult result = await TaskLedgerHolder.TryClaimAsync(
                ledger, project.RepositoryPath, taskId, new TaskRecordHolder(fingerprint, node.NodeId, nodeName, now), committer, signingKey,
                cancellationToken);
            switch (result.Verdict)
            {
                case HolderClaimVerdict.Claimed:
                case HolderClaimVerdict.NoRecord:
                    return FeedbackStepHolderVerdict.Held;
                case HolderClaimVerdict.HeldByOther:
                    logger.LogInformation(
                        "Task {TaskId}: another node ({HolderNode}) holds its ledger record, so its feedback step is that node's", taskId,
                        result.Holder?.NodeName);
                    return FeedbackStepHolderVerdict.HeldByOther;
                default:
                    logger.LogWarning("Task {TaskId}: its feedback step waits: the ledger holder could not be written: {Reason}", taskId, result.FailureReason);
                    return FeedbackStepHolderVerdict.Failed;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Task {TaskId}: its feedback step waits: checking or writing its ledger holder threw", taskId);
            return FeedbackStepHolderVerdict.Failed;
        }
    }

    /// <summary>
    /// Records, in the same save as the step's outcome, that this node owes the task's holder back. The release itself runs
    /// after that save, so a daemon that stops in between would otherwise leave this node named as the holder with nothing to
    /// give it back: the ended step has left the feedback sweep, and the step took neither a lease nor the task's holder field
    /// for any other sweep to find. The row is the dispatcher's own pending release, which its lease sweep retries until the
    /// write lands, and <see cref="ReleaseAsync"/> deletes it once the release is settled.
    /// </summary>
    public void StageRelease(IDocumentSession session, Guid taskId, Guid projectId, DateTimeOffset now) =>
        session.Store(new TaskHolderReleasePending
        {
            Id = TaskHolderReleasePending.KeyFor(taskId, node.NodeId),
            TaskId = taskId,
            ProjectId = projectId,
            NodeId = node.NodeId,
            RecordedAt = now,
            LastFailureReason = "the feedback step ended and its holder release had not run yet",
        });

    /// <summary>
    /// Gives the holder back once the step is over, or once the begun event lost its race. Best effort, because the step's own
    /// outcome has landed: a write that cannot complete is recorded as the pending release the dispatcher's lease sweep retries,
    /// and a release that is settled deletes the row <see cref="StageRelease"/> recorded with the outcome.
    /// </summary>
    public async Task ReleaseAsync(Guid taskId, Guid projectId, CancellationToken cancellationToken)
    {
        string pendingKey = TaskHolderReleasePending.KeyFor(taskId, node.NodeId);
        string? failure = null;
        try
        {
            await using IDocumentSession session = store.LightweightSession();

            // This runs after the outcome event that made the task Queued, so the dispatcher on this node may already have claimed
            // it: a holder that is already this node is a reclaim to the dispatcher, and its claim lands as a lease. A lease naming
            // this node means the holder now belongs to that run, and clearing it would leave a running task with no holder for a
            // teammate's node to win, the same guard the dispatcher's own release path makes before it clears one.
            if (await session.LoadAsync<TaskLease>(taskId, cancellationToken) is { NodeId: var leaseNodeId }
                && leaseNodeId == node.NodeId)
            {
                await SettleAsync(session, pendingKey, cancellationToken);
                return;
            }

            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
            if (project is null)
            {
                await SettleAsync(session, pendingKey, cancellationToken);
                return;
            }

            LedgerFile existing = await ledger.ReadAsync(
                project.RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cancellationToken);
            if (!existing.Exists)
            {
                failure = existing.FetchFailed
                    ? "the ledger record could not be read: the fetch failed"
                    : null;
            }
            else
            {
                (LedgerCommitter committer, LedgerSigningKey signingKey, _) =
                    await DispatchEngine.ResolveLedgerIdentityAsync(session, node, ownerRootFingerprint: null, cancellationToken);
                HolderReleaseResult result = await TaskLedgerHolder.TryReleaseAsync(
                    ledger, project.RepositoryPath, taskId, node.NodeId, committer, signingKey, cancellationToken);
                failure = result.Verdict == HolderReleaseVerdict.Failed
                    ? result.FailureReason ?? "the release failed"
                    : null;
            }

            if (failure is null)
            {
                await SettleAsync(session, pendingKey, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
        }

        if (failure is null)
        {
            return;
        }

        logger.LogWarning("Task {TaskId}: releasing the ledger holder after its feedback step failed: {Reason}. It is retried.", taskId, failure);
        try
        {
            await using IDocumentSession pending = store.LightweightSession();
            pending.Store(new TaskHolderReleasePending
            {
                Id = pendingKey,
                TaskId = taskId,
                ProjectId = projectId,
                NodeId = node.NodeId,
                RecordedAt = DateTimeOffset.UtcNow,
                LastFailureReason = failure,
            });
            await pending.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Task {TaskId}: could not record the ledger holder release as pending", taskId);
        }
    }

    /// <summary>The release is settled, so the pending row staged with the outcome (if any) has nothing left to retry.</summary>
    private static async Task SettleAsync(IDocumentSession session, string pendingKey, CancellationToken cancellationToken)
    {
        session.Delete<TaskHolderReleasePending>(pendingKey);
        await session.SaveChangesAsync(cancellationToken);
    }
}
