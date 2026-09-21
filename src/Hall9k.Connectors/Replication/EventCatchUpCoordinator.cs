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
    /// from <see cref="TrustChain.OwnerChains"/>'s own fleets (<see cref="TrustedOwner.FleetNodeIds"/>
    /// — the root's own node, when the ledger names it, plus every vouched node) — the only local
    /// source of "whose node is this" a receiver has, since node identity is node-scoped and never
    /// itself travels (idea 202383dc: "node- and owner-scoped events stay home"). Null when no
    /// chain this project currently trusts names this node id at all.</summary>
    private static MembershipRole? ResolveMemberRole(Guid candidateNodeId, TrustChain trustChain)
    {
        foreach ((string ownerFingerprint, TrustedOwner owner) in trustChain.OwnerChains)
        {
            if (owner.FleetNodeIds().Contains(candidateNodeId))
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
        // SinceGlobalSequence null is part of what makes this the bootstrap shape rather than
        // h9k project pull's own (self-review, task a56cf16e): both carry a null origin node and a
        // null stream, so without this clause an outstanding — or merely recent — project pull
        // would read as an outstanding bootstrap and suppress the one a brand-new node depends on.
        EventCatchUpRequest? mostRecent = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForOriginNodeId == null
                && request.ForStreamId == null && request.SinceGlobalSequence == null)
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
    /// <c>--from-jira</c> of a record whose stream is absent locally), <c>h9k task pull</c>, and the
    /// dependency asks <see cref="TaskDependencyCatchUp"/> mints when a landed task names a
    /// dependency this node does not hold. The CLI shapes run with no live trust chain or transport
    /// to rank candidates from (they never touch git or a network of their own). Every project
    /// member's own daemon sweep answers if it can; double answers are harmless by dedupe.
    /// <para>
    /// What it does about an earlier request for the same stream is <see cref="StreamRequestDecision"/>'s
    /// own rule, not this method's: an outstanding one suppresses this ask (or, with
    /// <paramref name="again"/>, is closed out as superseded and replaced), and a CLOSED one — answered,
    /// declined, or itself superseded — is not an ask in flight and is asked again. <paramref name="reMintCooldown"/>
    /// is null for a human's own ask and a real span for an automatic one, so a dependency nobody in
    /// the project holds is not asked for afresh every time any task lands.
    /// </para>
    /// </summary>
    public async Task<StreamRequestOutcome> RequestStreamBroadcastAsync(
        IDocumentSession session, Guid projectId, Guid forStreamId, Guid myNodeId, string myOwnerFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken, bool again = false, TimeSpan? reMintCooldown = null,
        Guid? forDependencyOfTaskId = null)
    {
        IReadOnlyList<EventCatchUpRequest> prior = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForStreamId == forStreamId)
            .ToListAsync(cancellationToken);

        StreamRequestOutcome outcome = StreamRequestDecision.Decide(prior, again, reMintCooldown, now);
        if (!outcome.Queues())
        {
            return outcome;
        }

        EventCatchUpRequest request = new()
        {
            Id = DomainId.New(),
            ProjectId = projectId,
            ForStreamId = forStreamId,
            Candidates = [],
            SentAt = now,
            ForDependencyOfTaskId = forDependencyOfTaskId,
        };

        // Closed out before the replacement is broadcast, and by supersede rather than by answer:
        // the old request was never answered, and the audit trail has to say which of the two
        // actually happened to it (AGENTS.md: never guess at unobserved facts).
        foreach (EventCatchUpRequest stale in prior.Where(candidate => candidate.IsOutstanding))
        {
            stale.SupersededAt = now;
            stale.SupersededByRequestId = request.Id;
            session.Store(stale);
        }

        await BroadcastAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Starts a broadcast request for a whole project's history at or above
    /// <paramref name="sinceGlobalSequence"/> on whichever peer answers (0 for <c>--since all</c>) —
    /// <c>h9k project pull</c>, the lever for an ESTABLISHED node, which is precisely the node the
    /// automatic bootstrap can never help: <c>MessageSweepEngine.HasAnyLocalHistoryAsync</c> gates
    /// that bootstrap to a node with no applied history at all, so a node that joined, caught up on
    /// the retention window, and has produced work of its own since never asks for anything older
    /// again. Broadcast rather than a ranked cascade for the identical reason
    /// <see cref="RequestStreamBroadcastAsync"/> is: this runs from a CLI command with no live trust
    /// chain or transport of its own (task a56cf16e).
    /// <para>
    /// Skipped when an outstanding project-history request already reaches at least as far back as
    /// this one would — a repeat of the same pull reports the one already in flight rather than
    /// queueing a second identical ask, while a genuinely deeper pull (a lower bound) is a
    /// different question and gets asked. A bootstrap request is never matched here: it carries a
    /// null <see cref="EventCatchUpRequest.SinceGlobalSequence"/>, and the two are deliberately
    /// distinct shapes.
    /// </para>
    /// </summary>
    public async Task<bool> RequestProjectHistoryBroadcastAsync(
        IDocumentSession session, Guid projectId, long sinceGlobalSequence, Guid myNodeId, string myOwnerFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool alreadyOutstanding = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.SinceGlobalSequence != null
                && request.SinceGlobalSequence <= sinceGlobalSequence
                && request.AnsweredAt == null && request.SupersededAt == null && !request.Exhausted)
            .AnyAsync(cancellationToken);
        if (alreadyOutstanding)
        {
            return false;
        }

        EventCatchUpRequest request = new()
        {
            Id = DomainId.New(),
            ProjectId = projectId,
            SinceGlobalSequence = sinceGlobalSequence,
            Candidates = [],
            SentAt = now,
        };
        await BroadcastAsync(session, myNodeId, myOwnerFingerprint, request, now, cancellationToken);
        return true;
    }

    /// <summary>Persists <paramref name="request"/> and queues its one project-wide
    /// <see cref="MessageKind.EventsRequest"/> envelope — the half both broadcast shapes share.
    /// The envelope's own <c>about</c> field carries the requested stream id when there is one and
    /// nothing otherwise: it is documented as a task or idea id a reader can act on, so a
    /// project-history pull's sequence bound does not belong in it — that bound rides the request
    /// body, where it is actually read.</summary>
    private static async Task BroadcastAsync(
        IDocumentSession session, Guid myNodeId, string myOwnerFingerprint, EventCatchUpRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Store(request);
        await MessageOutbox.QueueAsync(
            session, myNodeId, request.ProjectId, myOwnerFingerprint, MessageAudience.Project,
            about: request.ForStreamId?.ToString(),
            MessageKind.EventsRequest,
            EventReplicationCodec.EncodeRequest(new EventReplicationCodec.EventsRequestRecord(
                request.Id, ForOriginNodeId: null, SinceOriginSequence: 0, request.ForStreamId,
                request.SinceGlobalSequence)),
            now, cancellationToken);
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
            .Where(request => request.ProjectId == projectId && request.AnsweredAt == null
                && request.SupersededAt == null && !request.Exhausted)
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
                request.Id, request.ForOriginNodeId, request.SinceOriginSequence, request.ForStreamId,
                request.SinceGlobalSequence)),
            now, cancellationToken);
    }
}
