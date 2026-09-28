using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// The owner-act request's own reaction loop (idea 6be68ee2, companion 1bb803e1: "minting is a
/// request, not a local capability"): shaped exactly like <see cref="ClaimRequestWatchLoop"/> - its
/// own hosted service, the ordinary sweep cadence (<see cref="DaemonOptions.PollInterval"/>), decode,
/// mark handled on a permanent refusal, retry only on a non-domain exception - because it answers the
/// identical shape of ask, a member's own node asking a holder (there, a task's holder; here, the
/// node holding an owner's own highest-ranked live root key) to act on its behalf.
/// <para>
/// Two reactions live here, on either side of the exchange: a node holding a live root key answering
/// a <see cref="MessageKind.OwnerActRequest"/> it received (<see cref="ReactToRequestAsync"/>) - the
/// one node any given request is ever addressed to, so nothing else in the fleet ever reacts to it -
/// and the requesting node completing the invite it minted once a <see cref="MessageKind.OwnerActOutcome"/>
/// reply comes back naming the write done (<see cref="ReactToOutcomeAsync"/>). A third, background job
/// runs on every tick regardless of what is currently pending: re-checking every owner-role write this
/// node itself is holding for its own human's approval, and answering <c>expired</c> once the same
/// ten-minute wait a fresh request gets has passed with nobody approving it (<see cref="ExpireStaleHoldsAsync"/>).
/// </para>
/// </summary>
public sealed class OwnerActRequestWatchLoop(
    IDocumentStore store,
    NodeContext node,
    MessageNodeIdentityResolver identityResolver,
    ILedger ledger,
    ILedgerChainReader chainReader,
    NodeKeyStore keyStore,
    IOptions<DaemonOptions> options,
    ILogger<OwnerActRequestWatchLoop> logger) : BackgroundService
{
    /// <summary>
    /// How long an owner-act request may sit unanswered before the root gives up on it - generous
    /// against the five-second poll (idea 6be68ee2). Checked both against a fresh request's own
    /// <see cref="MessageDetails.QueuedAt"/> (the requester's own send time, carried through on the
    /// received copy) and against a still-open <see cref="OwnerActHeld"/> hold's own
    /// <see cref="OwnerActHoldDetails.HeldAt"/> - the identical wait, whichever side of it produced
    /// the delay. Also what settles a late answer after a promotion: a live root key stays live under
    /// idea 6be68ee2's own ranked set even once a successor has rotated in, so a late reply from it is
    /// cryptographically valid, not a forgery - this is what stops it from acting once the operator
    /// waiting on the other end has already moved on.
    /// </summary>
    internal static readonly TimeSpan RequestWait = TimeSpan.FromMinutes(10);

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
                logger.LogWarning(exception, "Owner-act sweep failed; will check again next tick");
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
            // No owner root fingerprint yet — nothing to answer as and nothing of this node's own to
            // complete either, the same "nothing to do yet" ClaimRequestWatchLoop's own check reads.
            return;
        }

        string requestKind = MessageKind.OwnerActRequest.Value;
        string outcomeKind = MessageKind.OwnerActOutcome.Value;
        IReadOnlyList<MessageDetails> pending = await lookupSession.Query<MessageDetails>()
            .Where(message => (message.Kind == requestKind || message.Kind == outcomeKind)
                && message.ReceivedAt != null && message.HandledAt == null)
            .ToListAsync(cancellationToken);

        foreach (MessageDetails message in pending)
        {
            try
            {
                if (message.Kind == requestKind)
                {
                    await ReactToRequestAsync(message, identity, now, cancellationToken);
                }
                else
                {
                    await ReactToOutcomeAsync(message, nodeId, now, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Reacting to owner-act message {MessageId} failed; will retry next sweep", message.Id);
            }
        }

        await ExpireStaleHoldsAsync(identity, now, cancellationToken);
    }

    /// <summary>
    /// The node addressed by a request answers it: verified, expired, refused (no longer a live root
    /// key), a member-role write performed automatically, or an owner-role write held for this node's
    /// own human. Every path ends in <see cref="MarkHandledAsync"/> — there is nothing here a later
    /// sweep tick would ever decide differently, the identical "a permanent refusal marks the message
    /// handled" posture <see cref="ClaimRequestWatchLoop"/> already documents.
    /// </summary>
    private async Task ReactToRequestAsync(
        MessageDetails message, MessageNodeIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OwnerActEnvelopeCodec.OwnerActRequestRecord? request =
            message.Body is null ? null : OwnerActEnvelopeCodec.TryDecodeRequest(message.Body);
        if (request is null)
        {
            logger.LogWarning("Owner-act request {MessageId} could not be decoded — marked handled without acting", message.Id);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        // The identical stale/misdirected guard ClaimRequestWatchLoop.ReactAsync applies to a claim
        // request: message.FromNodeId is the one field MessageInbox already authenticated, and a
        // mismatch against the body's own self-declared requester means this envelope was forged or
        // corrupted — never something to act on, whatever else it claims.
        if (message.FromNodeId != request.RequesterNodeId)
        {
            logger.LogWarning(
                "Owner-act request {MessageId} claims requester node {ClaimedRequesterNodeId}, but it was actually "
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
                "Owner-act request {MessageId} names project {ProjectId}, which this node does not know — marked "
                + "handled without acting", message.Id, message.ProjectId);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        string? senderFingerprint = await NodeSelfAnnouncedKeyResolver.ResolveFingerprintAsync(
            ledger, project.RepositoryPath, message.FromNodeId, cancellationToken);
        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);

        // ClaimRequestWatchLoop.IsRequesterOwnerVerified's own primitive, called here with THIS
        // node's own owner fingerprint — never request.RequesterOwnerFingerprint, which does not
        // exist on this codec at all (idea 6be68ee2: "using the root's OWN owner fingerprint, never a
        // fingerprint from the request body") — so a node belonging to a different owner's fleet can
        // never request a write against this owner's own membership merely by addressing this node.
        bool requesterVerified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(
            chain, senderFingerprint, identity.OwnerRootFingerprint, message.FromNodeId);
        bool expired = now - message.QueuedAt > RequestWait;
        string myFingerprint = NodeKeyStore.Fingerprint(identity.PublicKeyLine);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(myFingerprint, identity.OwnerRootFingerprint);

        OwnerActRequestVerdict verdict = Decide(requesterVerified, expired, isLiveRootKey, request.Role);
        switch (verdict)
        {
            case OwnerActRequestVerdict.RefuseNotVerified:
                logger.LogWarning(
                    "Owner-act request {MessageId} from node {FromNodeId} does not verify under this node's own "
                    + "owner {OwnerRootFingerprint}'s own chain — refused", message.Id, message.FromNodeId,
                    identity.OwnerRootFingerprint);
                await ReplyAsync(
                    session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                    OwnerActEnvelopeCodec.OwnerActVerdict.Refused, commitId: null,
                    "This node is not vouched under this owner's own chain.", now, cancellationToken);
                await MarkHandledAsync(message, now, cancellationToken);
                return;

            case OwnerActRequestVerdict.Expired:
                await ReplyAsync(
                    session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                    OwnerActEnvelopeCodec.OwnerActVerdict.Expired, commitId: null, reason: null, now, cancellationToken);
                await MarkHandledAsync(message, now, cancellationToken);
                return;

            case OwnerActRequestVerdict.RefuseNotLiveRootKey:
                // Something changed since this request was addressed here — succession rotated a
                // successor in, or this node's own vouch lapsed. Never retried automatically:
                // InviteSweepEngine.RequestMemberWriteAsync's own "already requested" guard is keyed
                // on this invite alone, not on the answer a prior request got, so a refusal here is
                // terminal for this invite's own automatic path — the requester's own h9k status
                // surfaces it (OwnerActAskLookup) for its human to act on, the same posture an
                // expired request already takes.
                await ReplyAsync(
                    session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                    OwnerActEnvelopeCodec.OwnerActVerdict.Refused, commitId: null,
                    $"This node no longer holds a live root key for owner {identity.OwnerRootFingerprint}.", now,
                    cancellationToken);
                await MarkHandledAsync(message, now, cancellationToken);
                return;

            case OwnerActRequestVerdict.PerformMemberWrite:
            {
                string? commitId = await MemberVouchLedgerWriter.WriteAsync(
                    ledger, project.RepositoryPath, request.CandidateOwnerFingerprint, request.Role, request.IssuedAt,
                    identity.Committer, identity.SigningKey, cancellationToken);
                await ReplyAsync(
                    session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                    OwnerActEnvelopeCodec.OwnerActVerdict.Done, commitId, reason: null, now, cancellationToken);
                await MarkHandledAsync(message, now, cancellationToken);
                return;
            }

            case OwnerActRequestVerdict.RefuseUnrecognizedRole:
                await ReplyAsync(
                    session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                    OwnerActEnvelopeCodec.OwnerActVerdict.Refused, commitId: null,
                    "This build does not recognize the role this request names.", now, cancellationToken);
                await MarkHandledAsync(message, now, cancellationToken);
                return;

            case OwnerActRequestVerdict.Hold:
                break;
        }

        // An owner-role write is held for this node's own human rather than performed automatically
        // (idea 6be68ee2's own ruling). Keyed by a Guid DERIVED from this exact message's own stream
        // id (OwnerActHoldStreamId.ForRequest) — never message.Id directly, which already addresses
        // that message's own MessageAggregate stream; starting a second stream under the identical
        // Guid would collide with it. A resend of the identical request (same invite, same content)
        // lands on a fresh MessageDetails row with its own id, so FetchStreamStateAsync below finding
        // nothing is simply "the first time THIS envelope was seen", never a reason to hold the same
        // write twice; h9k status and h9k project member approve both read the resulting
        // OwnerActHoldDetails row.
        Guid holdId = OwnerActHoldStreamId.ForRequest(message.Id);
        StreamState? existingHoldFence = await session.Events.FetchStreamStateAsync(holdId, cancellationToken);
        if (existingHoldFence is null)
        {
            session.Events.StartStream<OwnerActHoldAggregate>(
                holdId,
                OwnerActHoldDecider.Hold(
                    holdId, project.Id, project.RepositoryPath, request.InviteId, message.FromNodeId,
                    request.CandidateOwnerFingerprint, request.Role, request.IssuedAt, now));
            await session.SaveChangesAsync(cancellationToken);
            await ReplyAsync(
                session, project, message.FromNodeId, request.InviteId, identity.OwnerRootFingerprint,
                OwnerActEnvelopeCodec.OwnerActVerdict.Held, commitId: null, reason: null, now, cancellationToken);
        }

        await MarkHandledAsync(message, now, cancellationToken);
    }

    /// <summary>
    /// The requesting node completes its own invite once the root's own outcome names the write
    /// done — writing the spend the same way <c>InviteSweepEngine</c>'s own root-direct path already
    /// does, from this node's own locally-recorded <see cref="VouchedProjectRecord"/> (never anything
    /// carried on the wire — the candidate's own node id and key fingerprint were already committed to
    /// this node's own stream before the request was ever sent, the identical "commit locally before
    /// the external write" ordering <c>InviteSweepEngine.TryClaimAsync</c> already follows). A held,
    /// refused, or expired outcome writes nothing: a held reply is not terminal (a later approval on
    /// the root sends a fresh <c>done</c> for the identical invite), and a refused or expired one
    /// leaves the invite outstanding for its own ordinary expiry to drop.
    /// </summary>
    private async Task ReactToOutcomeAsync(MessageDetails message, Guid nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OwnerActEnvelopeCodec.OwnerActOutcomeRecord? outcome =
            message.Body is null ? null : OwnerActEnvelopeCodec.TryDecodeOutcome(message.Body);
        if (outcome is null)
        {
            logger.LogWarning("Owner-act outcome {MessageId} could not be decoded", message.Id);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        if (outcome.Verdict != OwnerActEnvelopeCodec.OwnerActVerdict.Done)
        {
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        InviteAggregate? aggregate = await session.Events.AggregateStreamAsync<InviteAggregate>(outcome.InviteId, token: cancellationToken);
        if (aggregate is null || aggregate.Spent || aggregate.ProjectId is not { } projectId
            || !aggregate.VouchedProjects.TryGetValue(projectId, out VouchedProjectRecord? vouched))
        {
            // Stale, already settled by an earlier delivery of the identical outcome, or this node
            // has no local record of ever requesting this write — nothing left to complete.
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        if (project is null)
        {
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        NodeSigningKey key = await keyStore.EnsureAsync(nodeId, cancellationToken);
        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No owner {node.OwnerId} for this owner-act reaction.");
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{nodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        await InviteSpendLedgerWriter.WriteAsync(
            ledger, project.RepositoryPath, aggregate.MinterOwnerFingerprint, aggregate.Id, aggregate.SecretHash,
            aggregate.Claim, aggregate.Role, aggregate.ExpiresAt, committer, signingKey, cancellationToken);

        session.Events.Append(
            aggregate.Id, InviteDecider.Spend(aggregate, vouched.CandidateNodeId, vouched.CandidateOwnerFingerprint, now));
        await session.SaveChangesAsync(cancellationToken);

        await MarkHandledAsync(message, now, cancellationToken);
    }

    /// <summary>
    /// Every owner-act hold this node is itself still sitting on, whatever request produced it,
    /// re-checked on every tick regardless of what this tick's own pending-message query found — a
    /// hold with nobody approving it must expire on its own clock, not only when a fresh message
    /// happens to arrive to remind this loop it exists.
    /// </summary>
    private async Task ExpireStaleHoldsAsync(MessageNodeIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        IReadOnlyList<OwnerActHoldDetails> outstanding = await session.Query<OwnerActHoldDetails>()
            .Where(hold => !hold.Approved && !hold.Expired)
            .ToListAsync(cancellationToken);

        foreach (OwnerActHoldDetails hold in outstanding)
        {
            if (now - hold.HeldAt <= RequestWait)
            {
                continue;
            }

            try
            {
                await ExpireHoldAsync(session, hold, identity, now, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Expiring owner-act hold {HoldId} failed; will retry next sweep", hold.Id);
            }
        }
    }

    private async Task ExpireHoldAsync(
        IDocumentSession session, OwnerActHoldDetails hold, MessageNodeIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StreamState fence = await session.Events.FetchStreamStateAsync(hold.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Owner-act hold {hold.Id} has no stream to expire.");
        OwnerActHoldAggregate aggregate = await session.Events.AggregateStreamAsync<OwnerActHoldAggregate>(
            hold.Id, version: fence.Version, token: cancellationToken)
            ?? throw new InvalidOperationException($"Owner-act hold {hold.Id} has no aggregate to expire.");
        if (aggregate.Approved || aggregate.Expired)
        {
            return;
        }

        session.Events.Append(hold.Id, expectedVersion: fence.Version + 1, OwnerActHoldDecider.Expire(aggregate, now));
        await session.SaveChangesAsync(cancellationToken);

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(hold.ProjectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        await ReplyAsync(
            session, project, hold.RequesterNodeId, hold.InviteId, identity.OwnerRootFingerprint,
            OwnerActEnvelopeCodec.OwnerActVerdict.Expired, commitId: null, reason: null, now, cancellationToken);
    }

    private async Task ReplyAsync(
        IDocumentSession session, ProjectDetails project, Guid requesterNodeId, Guid inviteId, string myOwnerFingerprint,
        string verdict, string? commitId, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OwnerActEnvelopeCodec.OwnerActOutcomeRecord record = new(inviteId, verdict, commitId, reason);
        await MessageOutbox.QueueAsync(
            session, node.NodeId, project.Id, myOwnerFingerprint, MessageAudience.Node(requesterNodeId),
            inviteId.ToString(), MessageKind.OwnerActOutcome, OwnerActEnvelopeCodec.Encode(record), now, cancellationToken);
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
    /// The root's own verdict on one request, pure and side-effect-free (the same reason
    /// <see cref="ClaimRequestWatchLoop.IsRequesterOwnerVerified"/> is its own static method):
    /// unit-testable without a document store, a ledger, or a daemon loop. Checked in this exact
    /// order: an unverified requester is refused before anything else is even asked, whether or not
    /// it is also expired or this node has since lost its own live root key; an expired request is
    /// answered <see cref="OwnerActRequestVerdict.Expired"/> next, before the live-root-key check —
    /// idea 6be68ee2's own ranked root-key set never revokes a key merely because a successor rotated
    /// in, so a late request is still cryptographically answerable by whichever live key it reached,
    /// and the wait is what stops that answer from acting once the operator on the other end has
    /// already moved on, regardless of whether this node could still technically act on it; only then
    /// does whether this node itself still holds a live root key decide anything.
    /// </summary>
    internal static OwnerActRequestVerdict Decide(bool requesterVerified, bool expired, bool isLiveRootKey, ProjectMemberRole role)
    {
        if (!requesterVerified)
        {
            return OwnerActRequestVerdict.RefuseNotVerified;
        }

        if (expired)
        {
            return OwnerActRequestVerdict.Expired;
        }

        if (!isLiveRootKey)
        {
            return OwnerActRequestVerdict.RefuseNotLiveRootKey;
        }

        if (role == ProjectMemberRole.Member)
        {
            return OwnerActRequestVerdict.PerformMemberWrite;
        }

        return role == ProjectMemberRole.Owner ? OwnerActRequestVerdict.Hold : OwnerActRequestVerdict.RefuseUnrecognizedRole;
    }
}

/// <summary>The closed set of verdicts <see cref="OwnerActRequestWatchLoop.Decide"/> ever returns.</summary>
internal enum OwnerActRequestVerdict
{
    RefuseNotVerified,
    Expired,
    RefuseNotLiveRootKey,
    PerformMemberWrite,
    Hold,
    RefuseUnrecognizedRole,
}
