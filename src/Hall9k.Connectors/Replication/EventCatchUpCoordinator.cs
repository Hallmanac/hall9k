using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// The outbound half of the catch-up protocol (idea 202383dc, M2b, task 9408d525): builds and
/// persists an <see cref="EventCatchUpRequest"/> and queues its first <see cref="MessageKind.EventsRequest"/>
/// envelope, ranks candidate peers, and cascades an outstanding request to the next candidate on
/// decline or timeout.
/// </summary>
public sealed class EventCatchUpCoordinator
{
    /// <summary>
    /// Ranks <paramref name="knownNodeIds"/> into the order idea 202383dc rules: the voucher first,
    /// then owner-role members, then any other member, most recently moved outbox first within a
    /// rank — the last part is this method's own precondition rather than something it computes:
    /// <paramref name="knownNodeIds"/> is expected already sorted by recency (most recently moved
    /// first) by the caller, since that recency is transport-probe state this pure function has no
    /// business owning; this method only re-groups that order into tiers, stably. A node this
    /// project's own trust chain does not currently recognize as a member (an unenrolled node, or
    /// one this ledger has never heard of) is never a candidate at all. <paramref name="myNodeId"/>
    /// is always excluded.
    /// </summary>
    public static IReadOnlyList<Guid> RankCandidates(
        IReadOnlyList<Guid> knownNodeIds, Guid myNodeId, Guid? voucherNodeId, TrustChain trustChain)
    {
        List<Guid> ordered = [];

        void AddIfEligible(Guid candidate)
        {
            if (candidate == myNodeId || ordered.Contains(candidate))
            {
                return;
            }

            if (ResolveMemberRole(candidate, trustChain) is null)
            {
                return;
            }

            ordered.Add(candidate);
        }

        if (voucherNodeId is { } voucher)
        {
            AddIfEligible(voucher);
        }

        foreach (Guid candidate in knownNodeIds)
        {
            if (ResolveMemberRole(candidate, trustChain) == MembershipRole.Owner)
            {
                AddIfEligible(candidate);
            }
        }

        foreach (Guid candidate in knownNodeIds)
        {
            AddIfEligible(candidate);
        }

        return ordered;
    }

    /// <summary>Which project member (if any) <paramref name="candidateNodeId"/> belongs to, read
    /// from <see cref="TrustChain.OwnerChains"/>'s own vouched-node lists — the only local source of
    /// "whose node is this" a receiver has, since node identity is node-scoped and never itself
    /// travels (idea 202383dc: "node- and owner-scoped events stay home"). Null when no chain this
    /// project currently trusts names this node id at all.</summary>
    private static MembershipRole? ResolveMemberRole(Guid candidateNodeId, TrustChain trustChain)
    {
        string candidateIdText = candidateNodeId.ToString();
        foreach ((string ownerFingerprint, TrustedOwner owner) in trustChain.OwnerChains)
        {
            if (owner.Nodes.Any(node => node.NodeId == candidateIdText))
            {
                return trustChain.RoleOf(ownerFingerprint);
            }
        }

        return null;
    }

    /// <summary>
    /// Starts a gap-fill request for one origin node's own missing history in one project — skipped
    /// when an outstanding, unanswered request for the identical (project, origin) pair already
    /// exists, or when the most recent one exhausted within <paramref name="reMintCooldown"/>, so a
    /// sender whose outbox keeps stalling on the same gap sweep after sweep does not mint a fresh
    /// request (and a fresh candidate cascade) every single tick — the identical unbounded-loop shape
    /// <see cref="RequestBootstrapAsync"/>'s own doc explains, sharing its own cause here too: a
    /// permanent numeric gap nobody holds the far side of has every candidate answer
    /// <see cref="MessageKind.EventsUnavailable"/> immediately, so an uncooled cascade exhausts within
    /// a few ticks and re-mints again next sweep, forever.
    /// </summary>
    public async Task<bool> RequestGapFillAsync(
        IDocumentSession session, Guid projectId, Guid forOriginNodeId, Guid myNodeId, string myOwnerFingerprint,
        IReadOnlyList<Guid> candidates, TimeSpan reMintCooldown, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EventCatchUpRequest? mostRecent = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForOriginNodeId == forOriginNodeId)
            .OrderByDescending(request => request.SentAt)
            .FirstOrDefaultAsync(cancellationToken);
        bool blockedByRecentAttempt = mostRecent is not null
            && (mostRecent.IsOutstanding || now - mostRecent.SentAt < reMintCooldown);
        if (blockedByRecentAttempt || candidates.Count == 0)
        {
            return false;
        }

        EventOriginProgress? progress = await session.LoadAsync<EventOriginProgress>(
            EventReplicationStreamId.ForOriginProgress(projectId, forOriginNodeId), cancellationToken);

        EventCatchUpRequest request = new()
        {
            Id = DomainId.New(),
            ProjectId = projectId,
            ForOriginNodeId = forOriginNodeId,
            SinceOriginSequence = progress?.HighestOriginSequenceApplied ?? 0,
            Candidates = [.. candidates],
            CandidateIndex = 0,
            SentAt = now,
        };
        await SendCurrentCandidateAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// Starts a brand-new node's own bootstrap request for a whole project — skipped when an
    /// outstanding "everything" request (both <see cref="EventCatchUpRequest.ForOriginNodeId"/> and
    /// <see cref="EventCatchUpRequest.ForStreamId"/> null) already exists for this project, or when
    /// the most recent one exhausted within <paramref name="reMintCooldown"/>. The cooldown matters
    /// for a project that is genuinely, currently empty (right after <c>h9k project join</c>, before
    /// anyone has added a first task or idea): every peer answers <see cref="MessageKind.EventsUnavailable"/>
    /// immediately rather than timing out, so a cascade with no cooldown exhausts within a few ticks
    /// and re-mints again next sweep, forever — one signed commit and push per side, per tick, for as
    /// long as the project stays empty (independent pre-PR review, cycle 1, conformance lens, medium).
    /// </summary>
    public async Task<bool> RequestBootstrapAsync(
        IDocumentSession session, Guid projectId, Guid myNodeId, string myOwnerFingerprint,
        IReadOnlyList<Guid> candidates, TimeSpan reMintCooldown, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EventCatchUpRequest? mostRecent = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForOriginNodeId == null && request.ForStreamId == null)
            .OrderByDescending(request => request.SentAt)
            .FirstOrDefaultAsync(cancellationToken);
        bool blockedByRecentAttempt = mostRecent is not null
            && (mostRecent.IsOutstanding || now - mostRecent.SentAt < reMintCooldown);
        if (blockedByRecentAttempt || candidates.Count == 0)
        {
            return false;
        }

        EventCatchUpRequest request = new()
        {
            Id = DomainId.New(),
            ProjectId = projectId,
            Candidates = [.. candidates],
            CandidateIndex = 0,
            SentAt = now,
        };
        await SendCurrentCandidateAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// Starts a broadcast request for one specific stream, addressed to the whole project rather
    /// than one ranked peer — the ledger-record adoption path (<c>h9k task add --from-issue</c>/
    /// <c>--from-jira</c> of a record whose stream is absent locally), which runs from a CLI command
    /// with no live trust chain or transport to rank candidates from (never touches git or a network
    /// on its own). Every project member's own daemon sweep answers if it can; double answers are
    /// harmless by dedupe. Skipped when an outstanding request for this exact stream already exists.
    /// </summary>
    public async Task<bool> RequestStreamBroadcastAsync(
        IDocumentSession session, Guid projectId, Guid forStreamId, Guid myNodeId, string myOwnerFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool alreadyOutstanding = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForStreamId == forStreamId
                && request.AnsweredAt == null && !request.Exhausted)
            .AnyAsync(cancellationToken);
        if (alreadyOutstanding)
        {
            return false;
        }

        EventCatchUpRequest request = new()
        {
            Id = DomainId.New(),
            ProjectId = projectId,
            ForStreamId = forStreamId,
            Candidates = [],
            SentAt = now,
        };
        session.Store(request);
        await MessageOutbox.QueueAsync(
            session, myNodeId, projectId, myOwnerFingerprint, MessageAudience.Project, about: forStreamId.ToString(),
            MessageKind.EventsRequest,
            EventReplicationCodec.EncodeRequest(new EventReplicationCodec.EventsRequestRecord(
                request.Id, ForOriginNodeId: null, SinceOriginSequence: 0, forStreamId)),
            now, cancellationToken);
        return true;
    }

    /// <summary>
    /// Cascades every outstanding, candidate-cascading request in one project whose current
    /// candidate has sat silent past <paramref name="timeout"/> to its next candidate — a broadcast
    /// request (<see cref="EventCatchUpRequest.Candidates"/> empty) never appears here, since it has
    /// no cascade to advance. Earlier requests are left standing (idea 202383dc: "with earlier
    /// requests left standing") — this only ever advances the ONE request whose timeout elapsed, and
    /// a candidate that eventually does answer late still lands and applies harmlessly.
    /// </summary>
    public async Task<int> AdvanceOverdueRequestsAsync(
        IDocumentSession session, Guid projectId, Guid myNodeId, string myOwnerFingerprint, TimeSpan timeout,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<EventCatchUpRequest> overdue = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.AnsweredAt == null && !request.Exhausted)
            .ToListAsync(cancellationToken);

        int advanced = 0;
        foreach (EventCatchUpRequest request in overdue)
        {
            if (request.Candidates.Count == 0 || now - request.SentAt < timeout)
            {
                continue;
            }

            await AdvanceToNextCandidateAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
            advanced++;
        }

        if (advanced > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
        }

        return advanced;
    }

    /// <summary>Moves <paramref name="request"/> to its next candidate and actually asks it
    /// (queuing a fresh <see cref="MessageKind.EventsRequest"/> envelope), or marks it
    /// <see cref="EventCatchUpRequest.Exhausted"/> once none remain — shared by an explicit decline
    /// (<see cref="EventCatchUpInbox"/>) and a silent timeout (<see cref="AdvanceOverdueRequestsAsync"/>),
    /// so a decline actually moves the cascade rather than merely recording that it should have
    /// (independent pre-PR review, cycle 1, adversarial lens, high: an earlier build here advanced
    /// <see cref="EventCatchUpRequest.CandidateIndex"/> without ever sending to the newly-current
    /// candidate, silently skipping it until the NEXT decline or timeout finally asked the one after
    /// it instead).</summary>
    internal static async Task AdvanceToNextCandidateAsync(
        IDocumentSession session, Guid myNodeId, string myOwnerFingerprint, EventCatchUpRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        request.CandidateIndex++;
        request.SentAt = now;
        if (request.CandidateIndex >= request.Candidates.Count)
        {
            request.Exhausted = true;
            session.Store(request);
            return;
        }

        await SendCurrentCandidateAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
    }

    private static async Task SendCurrentCandidateAsync(
        IDocumentSession session, Guid myNodeId, string myOwnerFingerprint, EventCatchUpRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Store(request);
        if (request.CurrentCandidateNodeId is not { } candidate)
        {
            return;
        }

        await MessageOutbox.QueueAsync(
            session, myNodeId, request.ProjectId, myOwnerFingerprint, MessageAudience.Node(candidate), about: null,
            MessageKind.EventsRequest,
            EventReplicationCodec.EncodeRequest(new EventReplicationCodec.EventsRequestRecord(
                request.Id, request.ForOriginNodeId, request.SinceOriginSequence, request.ForStreamId)),
            now, cancellationToken);
    }
}
