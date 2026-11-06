using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Persistence;
using Marten;

namespace Hall9k.Daemon.Feedback;

/// <summary>What asking the ledger for a task's holder came to, for the node that wants to begin its feedback step.</summary>
internal enum FeedbackStepHolderVerdict
{
    /// <summary>This node is the holder now (or the task has no ledger record, so there is nothing to guard): the step may begin here.</summary>
    Held,

    /// <summary>The record names another node, so that node's step is the one: this node stands down.</summary>
    HeldByOther,

    /// <summary>The ledger could not be read or written, so the step waits and a later sweep asks again.</summary>
    Failed,
}

/// <summary>The verdict, and, for one that is not <see cref="FeedbackStepHolderVerdict.Held"/>, why, for the caller to report once.</summary>
internal sealed record FeedbackStepHolderAnswer(FeedbackStepHolderVerdict Verdict, string? Why)
{
    public static readonly FeedbackStepHolderAnswer Held = new(FeedbackStepHolderVerdict.Held, null);
}

/// <summary>
/// The cross-node half of beginning a feedback step. Each node keeps its own Postgres, so the stream version that fences two
/// daemons of one database cannot stop two nodes of one owner from both beginning the step on a task with no placement. What
/// decides between nodes is the task's ledger record holder, the same conditional write the dispatcher's claim goes through
/// (<see cref="TaskLedgerHolder"/>): a node writes itself in before it appends the begun event, and a node that finds another
/// node named stands down. A holder write that cannot complete holds the step rather than letting it begin, the fail-closed rule
/// every holder write follows.
/// <para>
/// The holder is never given back from here. The step's ending save is what makes the task claimable, and the dispatcher on this
/// node writes the same holder when it claims, so a release run from this engine would be a check-then-act against that claim:
/// it could find no lease yet, then clear the holder the claim had just refreshed, leaving a running task unguarded. Instead the
/// ending save records the dispatcher's own pending release (<see cref="StageRelease"/>), and the dispatcher gives the holder
/// back on its next sweep, in the same loop as its claims, so the two can never overlap.
/// </para>
/// </summary>
internal sealed class FeedbackStepHolder(IDocumentStore store, NodeContext node, ILedger ledger, DaemonConnection connection)
{
    public async Task<FeedbackStepHolderAnswer> TryHoldAsync(
        IDocumentSession session, Guid taskId, ProjectDetails project, string? ownerRootFingerprint, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            LedgerFile existing = await ledger.ReadAsync(
                project.RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cancellationToken);
            if (existing.FetchFailed)
            {
                return new FeedbackStepHolderAnswer(
                    FeedbackStepHolderVerdict.Failed, "the ledger record could not be read to check for a holder, because the fetch failed");
            }

            if (!existing.Exists)
            {
                return FeedbackStepHolderAnswer.Held;
            }

            (LedgerCommitter committer, LedgerSigningKey signingKey, string fingerprint) =
                await DispatchEngine.ResolveLedgerIdentityAsync(session, node, ownerRootFingerprint, cancellationToken);
            string nodeName = (await session.LoadAsync<NodeDetails>(node.NodeId, cancellationToken))?.MachineName
                ?? Environment.MachineName;
            HolderClaimResult result = await TaskLedgerHolder.TryClaimAsync(
                ledger, project.RepositoryPath, taskId, new TaskRecordHolder(fingerprint, node.NodeId, nodeName, now), committer, signingKey,
                cancellationToken);
            return result.Verdict switch
            {
                HolderClaimVerdict.Claimed or HolderClaimVerdict.NoRecord => FeedbackStepHolderAnswer.Held,
                HolderClaimVerdict.HeldByOther => new FeedbackStepHolderAnswer(
                    FeedbackStepHolderVerdict.HeldByOther, $"another node ({result.Holder?.NodeName}) holds the task's ledger record"),
                _ => new FeedbackStepHolderAnswer(
                    FeedbackStepHolderVerdict.Failed, $"the ledger holder could not be written: {result.FailureReason}"),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FeedbackStepHolderAnswer(
                FeedbackStepHolderVerdict.Failed, $"checking or writing the task's ledger holder threw: {exception.Message}");
        }
    }

    /// <summary>
    /// Records, in the same save as the step's outcome, that this node owes the task's holder back. The row is the dispatcher's
    /// own pending release: its sweep gives the holder back (or, finding this node's lease on the task because it has already
    /// claimed it, leaves the holder to that run) and retries until the write lands, which also covers a daemon that stops
    /// straight after the outcome's save.
    /// </summary>
    public void StageRelease(IDocumentSession session, Guid taskId, Guid projectId, DateTimeOffset now) =>
        session.Store(new TaskHolderReleasePending
        {
            Id = TaskHolderReleasePending.KeyFor(taskId, node.NodeId),
            TaskId = taskId,
            ProjectId = projectId,
            NodeId = node.NodeId,
            RecordedAt = now,
            LastFailureReason = "the feedback step ended, and the dispatcher's sweep gives its holder back",
        });

    /// <summary>
    /// Records the release on its own, for a holder written for a begun event that then lost its race, so there is no outcome
    /// save to carry it, and wakes the dispatcher to give the holder back.
    /// </summary>
    public async Task StageReleaseAloneAsync(Guid taskId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StageRelease(session, taskId, projectId, now);
        await session.SaveChangesAsync(cancellationToken);
        await WakeDispatcherAsync(taskId, cancellationToken);
    }

    /// <summary>Rings the doorbell after a save that staged a release, so the dispatcher gives the holder back now rather than at its next poll.</summary>
    public Task WakeDispatcherAsync(Guid taskId, CancellationToken cancellationToken) =>
        Doorbell.RingAsync(connection.ConnectionString, $"feedback-step-holder-release:{taskId}", cancellationToken);
}
