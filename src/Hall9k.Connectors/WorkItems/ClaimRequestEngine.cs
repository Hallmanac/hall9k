using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Connectors.WorkItems;

/// <summary>What one cooperative claim request's own handling concluded.</summary>
public enum ClaimRequestVerdict
{
    /// <summary>Granted — the ledger holder was released for the requester and the domain event landed.</summary>
    Granted,

    /// <summary>Refused — a live run, or the holder's own human, said no.</summary>
    Refused,

    /// <summary>Parked for the holder's own human under <c>take-policy ask</c> — nothing decided yet.</summary>
    Parked,
}

/// <summary>
/// One request's whole answer. <see cref="TrackerFailureReason"/> is best-effort and never turns
/// a grant into a refusal (idea 202383dc, item 5's own criterion: "a failure sentence naming the
/// hand step") — it is set only alongside <see cref="ClaimRequestVerdict.Granted"/>, when the
/// gated tracker move itself could not be confirmed.
/// </summary>
public sealed record ClaimRequestOutcome(ClaimRequestVerdict Verdict, string? RefusalReason, string? TrackerFailureReason)
{
    public static readonly ClaimRequestOutcome Parked = new(ClaimRequestVerdict.Parked, null, null);

    public static ClaimRequestOutcome Granted(string? trackerFailureReason) =>
        new(ClaimRequestVerdict.Granted, null, trackerFailureReason);

    public static ClaimRequestOutcome Refused(string reason) => new(ClaimRequestVerdict.Refused, reason, null);
}

/// <summary>
/// The cooperative take's own shared engine (idea 202383dc, item 5, "a member can ask a holder
/// for a task"): the holder's own node reaches every method here whether it is answering
/// automatically on receipt of a claim-request envelope (<see cref="ReceiveRequestAsync"/>) or
/// through the holder's own human's <c>h9k task grant</c>/<c>h9k task refuse</c>
/// (<see cref="GrantAsync"/>/<see cref="RefuseAsync"/> directly) — "grant runs the auto release"
/// is exactly this: the CLI command and the automatic decision call the identical method.
/// <para>
/// Lives in Connectors, not the CLI or the daemon, because both reach it: <c>h9k task grant</c>/
/// <c>refuse</c> are CLI commands, and the on-receipt reaction is a daemon loop
/// (<c>ClaimRequestWatchLoop</c>) — Connectors is the one project both reference (AGENTS.md's own
/// reference graph).
/// </para>
/// </summary>
public static class ClaimRequestEngine
{
    /// <summary>
    /// A member asked this task's own holder for it — appended and, under
    /// <see cref="TakePolicy.Auto"/>, answered immediately: granted when no run is live for this
    /// task on this node, refused (naming the live run's own start time) otherwise. Under
    /// <see cref="TakePolicy.Ask"/> the request is only parked; <see cref="GrantAsync"/> and
    /// <see cref="RefuseAsync"/> are the holder's own human's two doors onto answering it.
    /// </summary>
    public static async Task<ClaimRequestOutcome> ReceiveRequestAsync(
        IDocumentStore store,
        IDocumentSession session,
        ProjectDetails project,
        Guid taskId,
        ClaimEnvelopeCodec.ClaimRequestRecord request,
        ILedger ledger,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        TrackerAssignmentTake? take,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StreamState fence = await FetchFenceAsync(session, taskId, cancellationToken);
        TaskAggregate task = await FetchAggregateAsync(session, taskId, fence, cancellationToken);

        // Checked before anything is appended (self-review, this branch): a claim-request envelope
        // is addressed to whichever node the requester believed held the task at send time, and the
        // holder can have moved on by the time this node's own sweep gets to it (this node released
        // it since, or never held it at all). GrantAsync/RefuseAsync both already refuse a caller
        // that is not the current holder, but only after this method would otherwise have appended
        // TaskTakeRequested first — parking a request no answer here could ever actually grant or
        // refuse, and re-appending it every retry for as long as the message stays unhandled.
        if (task.HolderNodeId != myNodeId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is not held by this node — a stale or misdirected claim request, "
                + "nothing recorded.");
        }

        TaskTakeRequested requested = TaskDecider.RequestTake(
            task, request.RequesterNodeId, request.RequesterOwnerId, request.RequesterOwnerFingerprint,
            request.Reason, now, request.RequesterTrackerIdentity);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, requested);
        await session.SaveChangesAsync(cancellationToken);

        if (project.TakePolicy == TakePolicy.Ask)
        {
            return ClaimRequestOutcome.Parked;
        }

        RunListItem? liveRun = await FindLiveRunAsync(session, taskId, myNodeId, cancellationToken);
        if (liveRun is not null)
        {
            string reason = $"a run is live for this task on this node, started at {liveRun.DispatchedAt:u}.";
            return await RefuseAsync(
                store, session, project, taskId, request.RequesterNodeId, request.RequesterOwnerId, reason,
                myNodeId, myOwnerFingerprint, now, cancellationToken);
        }

        return await GrantAsync(
            store, session, project, taskId, request.RequesterNodeId, request.RequesterOwnerId,
            request.RequesterTrackerIdentity, ledger, committer, signingKey, take, myNodeId, myOwnerFingerprint,
            now, cancellationToken);
    }

    /// <summary>
    /// Grants a cooperative take, whether reached automatically from
    /// <see cref="ReceiveRequestAsync"/> or from <c>h9k task grant</c>: releases the ledger holder
    /// by conditional write, appends the domain event naming the requester, moves the gated
    /// project's tracker assignee best-effort, and queues the <see cref="MessageKind.ClaimGranted"/>
    /// reply. Refuses when this node no longer holds the task, or (the CLI-only path) when there is
    /// no pending request to answer at all.
    /// </summary>
    public static async Task<ClaimRequestOutcome> GrantAsync(
        IDocumentStore store,
        IDocumentSession session,
        ProjectDetails project,
        Guid taskId,
        Guid requesterNodeId,
        Guid requesterOwnerId,
        string? requesterTrackerIdentity,
        ILedger ledger,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        TrackerAssignmentTake? take,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StreamState fence = await FetchFenceAsync(session, taskId, cancellationToken);
        TaskAggregate task = await FetchAggregateAsync(session, taskId, fence, cancellationToken);

        if (task.HolderNodeId != myNodeId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is not held by this node — a cooperative grant can only release a "
                + "holder this node itself currently is.");
        }

        // TaskDecider.GrantTake's own state guard runs before anything external is written
        // (independent pre-PR review, cycle 1, both lenses — the same "state/reason guard runs
        // before anything external is written" discipline TaskTakeCommand's own --force path
        // already follows): a task whose ledger holder this node still names but whose own state
        // has moved past Claimed/NeedsHuman (Done awaiting closeout, AwaitingAuthor on a pr-review
        // follow-through, Failed) has no holder for GrantTake to release, and finding that out only
        // after the ledger release already landed would leave the ledger holder-less with nothing
        // ever recorded on the task's own stream to show for it — the exact double-claim hazard
        // the previousHolder/rollback machinery below exists to prevent, not reintroduce. This
        // call's own event is discarded and rebuilt below once "now" and the requester are the
        // same either way, so nothing here is thrown away that the later call could not redo.
        _ = TaskDecider.GrantTake(task, requesterNodeId, requesterOwnerId, now);

        // Read ahead of the release so a lost append race below (this task's own stream moved
        // concurrently between the release and the commit) has something to restore: the same
        // window TaskTakeCommand's own --force path guards against elaborately, for the identical
        // reason — a release that lands but is never recorded on the domain stream would leave the
        // ledger holder-less and freely claimable by any node's ordinary dispatch sweep, while this
        // task's own aggregate still names myNodeId, a genuine double-claim hazard.
        TaskRecordHolder? previousHolder = await ReadCurrentHolderAsync(ledger, project.RepositoryPath, taskId, cancellationToken);

        HolderReleaseResult release = await TaskLedgerHolder.TryReleaseAsync(
            ledger, project.RepositoryPath, taskId, myNodeId, committer, signingKey, cancellationToken);
        if (release.Verdict == HolderReleaseVerdict.Failed)
        {
            throw new DomainConflictException(
                $"Task {taskId}: the ledger holder could not be released for the grant — "
                + $"{release.FailureReason} Nothing was recorded; the request stays pending.");
        }

        TaskHolderReleased granted = TaskDecider.GrantTake(task, requesterNodeId, requesterOwnerId, now);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, granted);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            bool rolledBack = await RestoreReleasedHolderBestEffortAsync(
                ledger, project.RepositoryPath, taskId, previousHolder, committer, signingKey, cancellationToken);
            throw new DomainConflictException(
                rolledBack
                    ? $"Task {taskId} changed while recording this grant — the ledger release was rolled "
                        + "back rather than left holder-less for a task this node's own stream never "
                        + "recorded releasing. Re-run once that settles."
                    // Deliberately never claims "it is now holder-less": the reclaim above can also
                    // fail because a third node's own ordinary claim already landed on the
                    // now-released record in the gap, in which case the ledger names THAT node, not
                    // no one — this node's own stream is simply left disagreeing with it either way.
                    : $"Task {taskId} changed while recording this grant, and the ledger release could not "
                        + "be rolled back onto this node — check h9k task show for who the ledger actually "
                        + "names now, since this node's own stream never recorded releasing it.");
        }

        // Run only after the grant's own domain event is durably appended (independent pre-PR
        // review, cycle 5, adversarial lens, medium): moving the tracker assignee is a one-way,
        // best-effort side effect with no rollback of its own, unlike the ledger release above —
        // running it before the append risked handing the tracker to the requester permanently
        // while the rollback above undid everything else, leaving a task the ledger and the domain
        // stream both agree H still holds, but whose tracker card already shows R as assignee. There
        // is nothing about this move that needs to precede the append; it reads exactly the same
        // task and project either way.
        string? trackerFailureReason = await GrantTrackerAssigneeBestEffortAsync(
            store, project, task, requesterTrackerIdentity, take, cancellationToken);

        await MessageOutbox.QueueAsync(
            session, myNodeId, project.Id, myOwnerFingerprint, MessageAudience.Node(requesterNodeId),
            taskId.ToString(), MessageKind.ClaimGranted,
            ClaimEnvelopeCodec.Encode(new ClaimEnvelopeCodec.ClaimGrantedRecord(taskId)), now, cancellationToken);

        return ClaimRequestOutcome.Granted(trackerFailureReason);
    }

    /// <summary>The holder a grant's own release is about to clear, captured ahead of time so a lost append race has something to restore.</summary>
    private static async Task<TaskRecordHolder?> ReadCurrentHolderAsync(
        ILedger ledger, string repositoryPath, Guid taskId, CancellationToken cancellationToken)
    {
        LedgerFile current = await ledger.ReadAsync(
            repositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cancellationToken);
        return current is { Exists: true, FetchFailed: false } ? TaskRecord.TryParse(current.Content)?.Holder : null;
    }

    /// <summary>
    /// Best-effort undo of a grant's own ledger release: the record now shows no holder (the
    /// release already landed), so reclaiming <paramref name="previousHolder"/> — this node's own
    /// holder record, captured before the release — is <see cref="TaskLedgerHolder.TryClaimAsync"/>'s
    /// own "record shows no holder" write, not <see cref="TaskLedgerHolder.TryRestoreAsync"/>'s own
    /// "record currently names the taker" one, which would find nothing to restore from here and
    /// report <see cref="HolderReleaseVerdict.NotHeld"/> without ever writing anything back. Nothing
    /// to reclaim (no record captured before the release, or none was ever recorded) reports failure
    /// honestly rather than a false "rolled back".
    /// </summary>
    private static async Task<bool> RestoreReleasedHolderBestEffortAsync(
        ILedger ledger, string repositoryPath, Guid taskId, TaskRecordHolder? previousHolder,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        if (previousHolder is null)
        {
            return false;
        }

        try
        {
            HolderClaimResult reclaim = await TaskLedgerHolder.TryClaimAsync(
                ledger, repositoryPath, taskId, previousHolder, committer, signingKey, cancellationToken);
            return reclaim.Verdict == HolderClaimVerdict.Claimed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses a cooperative take, whether reached automatically (a live run) or from
    /// <c>h9k task refuse --reason</c>. Carries no ledger or claim change of its own.
    /// </summary>
    public static async Task<ClaimRequestOutcome> RefuseAsync(
        IDocumentStore store,
        IDocumentSession session,
        ProjectDetails project,
        Guid taskId,
        Guid requesterNodeId,
        Guid requesterOwnerId,
        string reason,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StreamState fence = await FetchFenceAsync(session, taskId, cancellationToken);
        TaskAggregate task = await FetchAggregateAsync(session, taskId, fence, cancellationToken);

        if (task.HolderNodeId != myNodeId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is not held by this node — only the current holder can refuse a "
                + "cooperative take request.");
        }

        TaskTakeRefused refused = TaskDecider.RefuseTake(task, requesterNodeId, requesterOwnerId, reason, now);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, refused);
        await session.SaveChangesAsync(cancellationToken);

        await MessageOutbox.QueueAsync(
            session, myNodeId, project.Id, myOwnerFingerprint, MessageAudience.Node(requesterNodeId),
            taskId.ToString(), MessageKind.ClaimRefused,
            ClaimEnvelopeCodec.Encode(new ClaimEnvelopeCodec.ClaimRefusedRecord(taskId, refused.Reason)), now,
            cancellationToken);

        return ClaimRequestOutcome.Refused(refused.Reason);
    }

    /// <summary>
    /// Gated projects move the tracker assignee to the requester's own account on grant, best
    /// effort (idea 202383dc, item 5's own criterion) — never blocks or reverses the grant itself,
    /// which has already released the ledger holder by the time this runs. Returns the failure
    /// sentence naming the hand step when the move could not be confirmed, or null when there was
    /// nothing to do (the project is not gated, the task carries no gated item) or the move landed.
    /// </summary>
    private static async Task<string?> GrantTrackerAssigneeBestEffortAsync(
        IDocumentStore store, ProjectDetails project, TaskAggregate task, string? requesterTrackerIdentity,
        TrackerAssignmentTake? take, CancellationToken cancellationToken)
    {
        if (project.ClaimGate == ClaimGate.Off || task.ExternalReference is null)
        {
            return null;
        }

        string tracker = task.ExternalReference.Provider == WorkItemProvider.Jira ? "Jira" : "GitHub";

        if (requesterTrackerIdentity.IsBlank())
        {
            return $"the requester's own tracker identity was not carried on its claim request, so this "
                + $"install could not move the {tracker} assignee — assign it to the requester by hand.";
        }

        string granteeIdentity = requesterTrackerIdentity;
        try
        {
            TrackerAssignmentTake tracking = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
            TrackerTake result = await tracking.GrantToAsync(
                store, project.ClaimGate, task.ExternalReference, project.RepositoryPath, granteeIdentity,
                cancellationToken);
            return result.Passes
                ? null
                : $"the {tracker} assignee could not be confirmed moved to the requester — "
                    + $"{result.RefusalLine} Assign it to them by hand.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"moving the {tracker} assignee to the requester failed — {exception.Message} Assign it "
                + "to them by hand.";
        }
    }

    /// <summary>
    /// The live run this node holds for this task, if any — the same four live states
    /// <c>RunSupervisor.StopRunsSupersededByTakeoverAsync</c> reads, the most recently dispatched
    /// first so its own <see cref="RunListItem.DispatchedAt"/> is what a refusal names as the run's
    /// own start time.
    /// </summary>
    public static Task<RunListItem?> FindLiveRunAsync(
        IQuerySession session, Guid taskId, Guid nodeId, CancellationToken cancellationToken) =>
        session.Query<RunListItem>()
            .Where(run => run.TaskId == taskId && run.NodeId == nodeId)
            .Where(run => run.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value, RunState.UnderReview.Value))
            .OrderByDescending(run => run.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<StreamState> FetchFenceAsync(IDocumentSession session, Guid taskId, CancellationToken cancellationToken) =>
        await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

    private static async Task<TaskAggregate> FetchAggregateAsync(
        IDocumentSession session, Guid taskId, StreamState fence, CancellationToken cancellationToken) =>
        await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
}
