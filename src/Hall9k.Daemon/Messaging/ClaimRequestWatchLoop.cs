using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// The cooperative take's own reaction loop (idea 202383dc, item 5, "a member can ask a holder for
/// a task"): polls this node's own received-but-unhandled messages of all three envelope kinds
/// <see cref="MessageKind.MechanicalKindValues"/> names on the ordinary sweep cadence
/// (<see cref="DaemonOptions.PollInterval"/>) — its own separate hosted service rather than a step
/// folded into <see cref="MessageSweepEngine"/>, the same reasoning
/// <see cref="Execution.TakeoverWatchLoop"/>'s own doc gives for keeping a reactive concern that is
/// not itself message transport apart from the transport sweep. No doorbell: nothing on this node
/// observes "a claim request just arrived" any sooner than the next poll.
/// <para>
/// A <see cref="MessageKind.ClaimRequest"/> is handed to
/// <see cref="ClaimRequestEngine.ReceiveRequestAsync"/> — the holder's own node deciding whether to
/// grant, refuse, or park. A <see cref="MessageKind.ClaimGranted"/> or
/// <see cref="MessageKind.ClaimRefused"/> reply carries nothing this node does not already have:
/// the requester's own answer arrives through the replicated <c>TaskHolderReleased</c> or
/// <c>TaskTakeRefused</c> event on the task's own stream, which is what <c>h9k task show</c> and
/// <c>h9k status</c> actually read (<see cref="ClaimEnvelopeCodec"/>'s own doc) — so this loop's
/// only job for a reply is to decode it far enough to log a malformed one, then mark it handled
/// rather than leave it received-and-invisible forever (independent pre-PR review, cycle 1,
/// adversarial lens: before this, nothing on the receiving node ever read or handled either reply
/// kind at all).
/// </para>
/// </summary>
public sealed class ClaimRequestWatchLoop(
    IDocumentStore store,
    NodeContext node,
    MessageNodeIdentityResolver identityResolver,
    ILedger ledger,
    ILedgerChainReader chainReader,
    IOptions<DaemonOptions> options,
    ILogger<ClaimRequestWatchLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Claim-request sweep failed; will check again next tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using IDocumentSession lookupSession = store.LightweightSession();
        MessageNodeIdentity? identity = await identityResolver.ResolveAsync(lookupSession, nodeId, node.OwnerId, cancellationToken);
        if (identity is null)
        {
            // No owner root fingerprint yet (h9k project join never ran) — the same "nothing to do
            // yet" MessageSweepEngine's own identical check reads.
            return;
        }

        IReadOnlyList<string> mechanicalKinds = MessageKind.MechanicalKindValues;
        IReadOnlyList<MessageDetails> pending = await lookupSession.Query<MessageDetails>()
            .Where(message => mechanicalKinds.Contains(message.Kind)
                && message.ReceivedAt != null && message.HandledAt == null)
            .ToListAsync(cancellationToken);

        foreach (MessageDetails message in pending)
        {
            try
            {
                await ReactAsync(message, nodeId, identity, now, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Reacting to claim request {MessageId} failed; will retry next sweep", message.Id);
            }
        }
    }

    private async Task ReactAsync(
        MessageDetails message, Guid nodeId, MessageNodeIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (message.Kind == MessageKind.ClaimGranted.Value || message.Kind == MessageKind.ClaimRefused.Value)
        {
            await ReactToReplyAsync(message, now, cancellationToken);
            return;
        }

        ClaimEnvelopeCodec.ClaimRequestRecord? request = message.Body is null
            ? null
            : ClaimEnvelopeCodec.TryDecodeRequest(message.Body);
        if (request is null)
        {
            logger.LogWarning("Claim request {MessageId} could not be decoded — marked handled without acting", message.Id);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        // The envelope body's own RequesterNodeId/RequesterOwnerId are the requester's own
        // self-declared claim, never verified by anything below this point — message.FromNodeId is
        // the one field MessageInbox itself already authenticated (against the outbox owner and the
        // envelope's own FromNode) before this message was ever stored. A mismatch means the body
        // names somebody other than whoever actually sent it — a forged or corrupted request — and
        // ReceiveRequestAsync must never be handed it: it would append TaskTakeRequested naming the
        // forged asker and, under take-policy auto, could hand the task to an owner id no node here
        // enumerates (independent pre-PR review, cycle 5, adversarial lens, medium; AGENTS.md's own
        // "never guess at unobserved facts" rule).
        if (message.FromNodeId != request.RequesterNodeId)
        {
            logger.LogWarning(
                "Claim request {MessageId} claims requester node {ClaimedRequesterNodeId}, but it was actually "
                + "sent by node {ActualSenderNodeId} — marked handled without acting",
                message.Id, request.RequesterNodeId, message.FromNodeId);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(message.ProjectId, cancellationToken);
        if (project is null)
        {
            logger.LogWarning(
                "Claim request {MessageId} names project {ProjectId}, which this node does not know — "
                + "marked handled without acting", message.Id, message.ProjectId);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        // request.RequesterOwnerId/RequesterOwnerFingerprint are the same shape of self-declared,
        // unverified pair RequesterNodeId was above. Node B's own outbox can only ever carry B's
        // own genuine content, but B still fully controls what that content claims about which
        // owner is asking; this is the one check available that does not just trust it back:
        // node.yaml is the identical self-announcement GitLedgerMessageTransport.ReadSinceAsync
        // already fingerprints to authenticate B's own outbox in the first place, so resolving it
        // again here and checking the claimed root against the ledger's own trust chain
        // (TrustedOwner.ContainsForNode, the same primitive IsAllowedSigner itself uses) proves the
        // claimed fingerprint is really B's own owner, never an owner B merely knows the
        // fingerprint of from this project's own replicated history. RequesterOwnerId itself stays
        // exactly as unverifiable as ClaimEnvelopeCodec's own doc already says it is — no registry
        // here or anywhere else in this project ever carries a remote owner's local Guid, since
        // Owner events are OwnerScoped and never travel (EventScopeRegistry) — so a request naming
        // a genuine fingerprint alongside a wrong RequesterOwnerId is not caught by this check
        // alone (independent pre-PR review, cycle 6, adversarial lens, medium). What closes it:
        // this exact verified value — request.RequesterOwnerFingerprint, already checked below —
        // is what TaskDecider.RequestTake records on TaskTakeRequested, what GrantTake carries
        // forward onto TaskHolderReleased.GrantedToOwnerFingerprint beside the self-declared
        // GrantedToOwnerId, and what the daemon's own dispatch claim gate compares against this
        // node's own owner fingerprint rather than the bare Guid (idea 20723ef8) — a forged
        // RequesterOwnerId can still land on the task's own stream as an audit fact, but it can no
        // longer steal a claim on the receiving node or block the true grantee's own.
        string? senderFingerprint = await NodeSelfAnnouncedKeyResolver.ResolveFingerprintAsync(
            ledger, project.RepositoryPath, message.FromNodeId, cancellationToken);
        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        if (!IsRequesterOwnerVerified(chain, senderFingerprint, request.RequesterOwnerFingerprint, message.FromNodeId))
        {
            logger.LogWarning(
                "Claim request {MessageId} claims requester owner root {ClaimedRequesterOwnerFingerprint}, but "
                + "the ledger's own trust chain does not vouch sender node {ActualSenderNodeId} under that "
                + "root — marked handled without acting",
                message.Id, request.RequesterOwnerFingerprint, message.FromNodeId);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        TrackerAssignmentTake take = new(new ProjectScopedGitHubRunner(store).Runner);
        try
        {
            await ClaimRequestEngine.ReceiveRequestAsync(
                store, session, project, request.TaskId, request, ledger, identity.Committer, identity.SigningKey,
                take, nodeId, identity.OwnerRootFingerprint, now, cancellationToken);
        }
        catch (DomainException exception)
        {
            // A permanent refusal marks the message handled rather than leaving it to retry
            // forever (independent pre-PR review, cycle 1 and 3, both lenses): the stale/misdirected
            // guard at the top of ReceiveRequestAsync and the state guard TaskDecider.GrantTake now
            // runs before any external write (ClaimRequestEngine.GrantAsync) are both refusals no
            // later sweep will ever decide differently, and re-processing them on every poll would
            // also re-append TaskTakeRequested each time. Even the one case in this family that IS
            // an external-system failure — the ledger holder could not be released for the grant —
            // is safe to mark handled the same way: TaskTakeRequested already landed on the task's
            // own stream by the time GrantAsync runs, so the request stays visible on h9k status
            // and h9k task show either way, and the holder's own human still has every ordinary
            // door onto it (h9k task grant/refuse, or the requester's own --force once it times
            // out) — marking this message handled only stops THIS automatic retry loop, not those.
            // Caught as the DomainException base, not only DomainConflictException (adversarial
            // pre-PR review, cycle 3): a version-skewed or hand-crafted envelope reaches
            // FetchFenceAsync's DomainNotFoundException (an unknown task id) or
            // TaskDecider.RequestTake's DomainValidationException (a blank reason) just as
            // permanently as any DomainConflictException, and the narrower catch left both jammed,
            // retrying and re-warning on every poll forever — the exact failure this commit set out
            // to end. An actual transient failure below ReceiveRequestAsync (a database hiccup) is
            // never a DomainException at all, so it still falls through to SweepOnceAsync's own
            // catch and retries next tick, unmarked.
            logger.LogWarning(
                exception, "Claim request {MessageId} refused; marked handled — retrying would not change "
                + "the outcome", message.Id);
        }

        await MarkHandledAsync(message, now, cancellationToken);
    }

    /// <summary>
    /// A <see cref="MessageKind.ClaimGranted"/> or <see cref="MessageKind.ClaimRefused"/> reply —
    /// see this class's own doc for why nothing here needs to act on one beyond decoding it far
    /// enough to log a malformed body, then marking it handled so it does not sit
    /// received-and-unhandled forever.
    /// </summary>
    private async Task ReactToReplyAsync(MessageDetails message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool decoded = message.Body is not null && (message.Kind == MessageKind.ClaimGranted.Value
            ? ClaimEnvelopeCodec.TryDecodeGranted(message.Body) is not null
            : ClaimEnvelopeCodec.TryDecodeRefused(message.Body) is not null);
        if (!decoded)
        {
            logger.LogWarning("Claim reply {MessageId} ({Kind}) could not be decoded", message.Id, message.Kind);
        }

        await MarkHandledAsync(message, now, cancellationToken);
    }

    private async Task MarkHandledAsync(MessageDetails message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(message.Id, cancellationToken);
        if (fence is null)
        {
            return;
        }

        MessageAggregate? aggregate = await session.Events.AggregateStreamAsync<MessageAggregate>(
            message.Id, version: fence.Version, token: cancellationToken);
        if (aggregate is null || aggregate.HandledAt is not null)
        {
            return;
        }

        session.Events.Append(message.Id, expectedVersion: fence.Version + 1, MessageDecider.Handle(aggregate, now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the ledger's own trust chain actually vouches <paramref name="senderNodeId"/> — the
    /// node <see cref="MessageInbox"/> already authenticated this envelope came from — under
    /// <paramref name="claimedRequesterOwnerFingerprint"/>, the root a claim request's own body
    /// self-declares as the asker's owner. <paramref name="senderFingerprint"/> is
    /// <paramref name="senderNodeId"/>'s own self-announced device key, resolved by the caller from
    /// its <c>node.yaml</c> the same way <c>GitLedgerMessageTransport.ReadSinceAsync</c> already
    /// resolves it to authenticate the sender in the first place — null when that resolution found
    /// nothing trustworthy, which never verifies. Pure and side-effect-free, the same reason
    /// <c>MessageSweepEngine.ResolveVoucherNodeId</c> is its own static method: unit-testable
    /// without a document store or a ledger.
    /// </summary>
    internal static bool IsRequesterOwnerVerified(
        TrustChain chain, string? senderFingerprint, string claimedRequesterOwnerFingerprint, Guid senderNodeId) =>
        senderFingerprint is not null
        && chain.OwnerChains.TryGetValue(claimedRequesterOwnerFingerprint, out TrustedOwner? claimedOwner)
        && claimedOwner.ContainsForNode(senderFingerprint, senderNodeId.ToString());
}
