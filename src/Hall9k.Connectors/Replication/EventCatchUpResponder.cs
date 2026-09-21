using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// Answers one <see cref="EventReplicationCodec.EventsRequestRecord"/> from what this node holds
/// (idea 202383dc, M2b, task 9408d525) — unlike <see cref="EventReplicationOutbox"/>'s own ordinary
/// flush, which only ever ships what this node itself produced, this forwards ANY project-scoped
/// event this node holds, own or already-replicated alike, with its own true origin preserved: a
/// peer that squashed old history out of its own outbox, or a brand-new node with none at all, can
/// still be caught up by whichever OTHER peer already applied it, not only by the original sender.
/// Two exclusions still apply, mirroring the ordinary flush: a currently-private task or idea's own
/// events never travel here either (<see cref="ReplicationOwnership.IsPrivate"/>), and an event this
/// node holds whose own true origin IS the requester is never handed back to it — the requester's
/// own dedupe only ever recognises an event it received by replication, never one it produced
/// natively, so an echo of its own history would apply as an un-deduped second copy.
/// <para>
/// A third exclusion, this node's own replication switch-on point, applies to a gap-fill alone. It
/// does not apply to a request that names what it wants
/// (<see cref="EventReplicationCodec.EventsRequestRecord.IsExplicitAsk"/>: one named stream, or a
/// named global sequence bound) — whoever minted it, a human through <c>h9k task pull</c> or the
/// daemon's own held-tail sweep (task c3bdb62e). Task a56cf16e, Decisions Log #236: history is
/// inert until something asks for it by name, and naming is the opt-in — the switch-on point
/// otherwise made every task published on a
/// node before that node switched replication on permanently unservable to its peers, with the
/// asking side told only "nothing held here matches this request". Nor does it apply to a
/// brand-new node's own bootstrap
/// (<see cref="EventReplicationCodec.EventsRequestRecord.IsBootstrap"/>), which is served whole
/// from the start of this node's log (task 74a7cd0b, Decisions Log #PLACEHOLDER-74a7cd0b,
/// superseding #236 for that one automatic shape). The two private exclusions above hold
/// regardless of which shape asked.
/// </para>
/// <para>
/// Queues one or more <see cref="MessageKind.Events"/> envelopes back to the requester, batched and
/// paginated the identical way <see cref="EventReplicationOutbox"/> caps an ordinary flush
/// (<see cref="EventReplicationOutbox.MaxEventsPerEnvelope"/>/<see cref="EventReplicationOutbox.MaxBytesPerEnvelope"/>) —
/// the receiving <c>EventReplicationInbox</c> applies them exactly as it applies any other events
/// envelope, idempotent by origin event id, so a double answer from two different candidates is
/// harmless. Queues one <see cref="MessageKind.EventsUnavailable"/> envelope instead when nothing
/// this node holds matches at all (idea 202383dc: "a peer that cannot answer says so").
/// </para>
/// <para>
/// An ask for one named stream is answered with that stream AND, when it is a task's, every run
/// stream this node holds for that task — see <see cref="ResolveRequestedStreamIdsAsync"/> for why a
/// task pull answered with the task stream alone lands a task that reads as though it never ran.
/// </para>
/// </summary>
public sealed class EventCatchUpResponder(ReplicationProjectResolver ownership, ILedger ledger)
{
    public async Task<int> AnswerAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid myNodeId,
        string myOwnerFingerprint,
        Guid projectId,
        Guid requesterNodeId,
        EventReplicationCodec.EventsRequestRecord request,
        DateTimeOffset now,
        TrustChain? trustChain,
        CancellationToken cancellationToken)
    {
        // idea 8c5993c5: the requester's own owner root, resolved from the live trust chain — null
        // when none was supplied, or when the requester is not vouched into any owner chain this
        // read knows about, in which case a fleet-scoped item's own match below always reads false
        // (nothing to compare) rather than guessing at a fleet membership nobody has proven.
        string? requesterOwnerRootFingerprint = await ResolveRequesterOwnerRootFingerprintAsync(
            ledger, repositoryPath, trustChain, requesterNodeId, cancellationToken);
        // Bounded to the requested stream and its own run streams when the request names one (the
        // gap-fill and bootstrap shapes still need the full scan, since neither names a stream up
        // front) — independent pre-PR review, cycle 1, both lenses, low: an unbounded scan of this
        // node's entire event log, on every answered request, was the worst case named there.
        List<Guid> requestedStreamIds = request.ForStreamId is { } forStreamId
            ? await ResolveRequestedStreamIdsAsync(session, forStreamId, cancellationToken)
            : [];
        IQueryable<IEvent> query = session.Events.QueryAllRawEvents();
        if (request.ForStreamId is not null)
        {
            query = query.Where(e => requestedStreamIds.Contains(e.StreamId));
        }
        else if (request.SinceGlobalSequence is { } sinceGlobalSequence)
        {
            query = query.Where(e => e.Sequence >= sinceGlobalSequence);
        }

        // Ascending by this node's own global sequence, the identical ordering
        // EventReplicationOutbox.QueuePendingAsync's own scan already applies. An unordered scan is
        // whatever order Postgres hands back, and the receiving EventReplicationInbox appends a
        // batch's records to a local stream in exactly the order it reads them — so this order IS
        // the order the requester's own stream ends up in, and the receiver's new
        // out-of-order refusal (that class's own OriginHighWaterAsync) would otherwise trip on a
        // shuffled answer's own head events (independent pre-PR review, cycle 4, adversarial lens,
        // high).
        IReadOnlyList<IEvent> candidates = await query.OrderBy(e => e.Sequence).ToListAsync(cancellationToken);

        // This node's own pre-replication history never travels on a RECURRING ask, in a catch-up
        // answer any more than an ordinary outbox flush (EventReplicationOutbox.QueuePendingAsync's
        // own identical Math.Max(position, switchOnSequence) exclusion) — independent pre-PR
        // review, cycle 1, conformance lens, high: this node's entire pre-switch-on back catalogue
        // riding every gap-fill a peer mints is what idea 202383dc's migration ruling keeps out of
        // scope.
        //
        // A request that NAMES what it wants (EventsRequestRecord.IsExplicitAsk: one named stream,
        // or a named global sequence bound) is the opt-in that lifts it, and only for that one
        // request — task a56cf16e's own origin incident, 2026-09-19: the Mac's ReplicationSwitchedOn
        // landed at global sequence 30084 while the task the Windows node was asking for sat at
        // 28273-30020, so every stream request for it answered "nothing held here matches this
        // request" and no re-run could ever have changed that. History stays inert until something
        // asks for it by name. Never lifted for an ordinary flush or a gap-fill.
        //
        // Naming, not a human's hand, is the rule: the held-tail ask (task c3bdb62e) is minted by a
        // daemon sweep and does lift it, because it names one stream this node already holds part
        // of and is asking the peer to complete history that already partly travelled — see
        // IsExplicitAsk's own doc for why that is the same case the exclusion was never meant to
        // strand, rather than a hole in it.
        //
        // A brand-new node's own bootstrap (EventsRequestRecord.IsBootstrap: no origin node, no
        // stream, no global sequence bound) lifts it as well, though it names nothing — task
        // 74a7cd0b, Decisions Log #PLACEHOLDER-74a7cd0b, which supersedes #236 for that one
        // automatic shape. Naming is one way to be bounded and it is not the only one: a
        // two-direction pull test on 2026-09-21 showed a node joining a project receives, from
        // every peer, only what that peer appended after its own switch-on point, so the tail of
        // work that predates it lands headless or not at all and the new member has no way to know
        // there is anything below to name. A bootstrap is minted once in a node's life, for a node
        // holding nothing of this project at all, so serving it whole costs one answer rather than
        // re-serving history on every tick. The gap-fill is what keeps the bound, and it is the one
        // shape bounded neither way: it names nothing AND it is minted whenever a hole is noticed,
        // on a node that already holds the project's recent history, which is where inertness still
        // matters.
        long? switchOnSequence = request.IsExplicitAsk || request.IsBootstrap
            ? null
            : await EventReplicationOutbox.EnsureSwitchedOnAsync(session, myNodeId, now, cancellationToken);

        List<EventReplicationCodec.ReplicatedEventRecord> matches = [];
        foreach (IEvent candidate in candidates)
        {
            if (switchOnSequence is { } excludedAtOrBelow && candidate.Sequence <= excludedAtOrBelow)
            {
                continue;
            }

            EventScope scope;
            try
            {
                scope = EventScopeRegistry.ClassificationOf(candidate.EventType);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (scope != EventScope.ProjectScoped)
            {
                continue;
            }

            ReplicationOwnership resolved = await ownership.ResolveAsync(session, candidate, cancellationToken);
            if (resolved.ProjectId != projectId)
            {
                continue;
            }

            if (resolved.IsProjectStreamItself && ProjectStreamReplicationRules.IsProjectIdentityEvent(candidate.EventType))
            {
                // Never eligible to travel at all — see EventReplicationOutbox.QueuePendingAsync's own doc.
                continue;
            }

            if (resolved.Scope == ReplicationScope.Private)
            {
                // A currently-private task or idea's own events never ride the ordinary outbox
                // flush (EventReplicationOutbox.QueuePendingAsync) — a catch-up answer must honor
                // the identical hold-back, never forwarding a draft a teammate should not see yet
                // just because this node happens to hold a copy of it (independent pre-PR review,
                // cycle 1, both lenses, high).
                continue;
            }

            (Guid originNodeId, string originOwnerRootFingerprint, Guid originEventId, long originSequence) =
                ReplicationEventOriginResolver.Resolve(candidate, myNodeId, myOwnerFingerprint);

            if (originNodeId == requesterNodeId)
            {
                // Never hand a node its own history back: the requester's own dedupe
                // (EventReplicationInbox.ApplyAsync) only recognises an event it received BY
                // REPLICATION, never one it produced natively, so an echoed-back event of its own
                // would apply as a second, un-deduped copy onto its own stream (independent pre-PR
                // review, cycle 1, conformance lens, high).
                continue;
            }

            // idea 8c5993c5: a fleet-scoped item answers only a requester of the SAME owner as the
            // item's own origin — the identical restriction the ordinary outbox flush enforces by
            // addressing the envelope to owner:<fingerprint> in the first place. A catch-up answer
            // is a unicast to one requester rather than a broadcast, so it has to ask the question
            // directly instead of relying on the envelope audience check every OTHER reader of this
            // outbox already applies.
            bool scopeAllowsRequester = resolved.Scope == ReplicationScope.Team
                || (resolved.Scope == ReplicationScope.Fleet
                    && requesterOwnerRootFingerprint is not null
                    && requesterOwnerRootFingerprint == originOwnerRootFingerprint);
            if (!scopeAllowsRequester)
            {
                continue;
            }

            bool isMatch = request switch
            {
                { ForStreamId: not null } => requestedStreamIds.Contains(candidate.StreamId),
                { ForOriginNodeId: { } forOriginNodeId } => originNodeId == forOriginNodeId && originSequence > request.SinceOriginSequence,
                _ => true,
            };
            if (!isMatch)
            {
                continue;
            }

            matches.Add(new EventReplicationCodec.ReplicatedEventRecord(
                candidate.StreamId,
                candidate.EventType.FullName ?? candidate.EventType.Name,
                System.Text.Json.JsonSerializer.Serialize(candidate.Data, candidate.EventType),
                originEventId,
                originSequence,
                originNodeId,
                originOwnerRootFingerprint,
                candidate.Timestamp,
                projectId));
        }

        if (matches.Count == 0)
        {
            await MessageOutbox.QueueAsync(
                session, myNodeId, projectId, myOwnerFingerprint, MessageAudience.Node(requesterNodeId), about: null,
                MessageKind.EventsUnavailable,
                EventReplicationCodec.EncodeUnavailable(new EventReplicationCodec.EventsUnavailableRecord(
                    request.RequestId, "nothing held here matches this request")),
                now, cancellationToken);
            return 0;
        }

        int envelopesQueued = 0;
        List<EventReplicationCodec.ReplicatedEventRecord> batch = [];
        long batchBytes = 0;
        foreach (EventReplicationCodec.ReplicatedEventRecord record in matches)
        {
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);
            if (batch.Count >= EventReplicationOutbox.MaxEventsPerEnvelope
                || (batch.Count > 0 && batchBytes + recordJson.Length > EventReplicationOutbox.MaxBytesPerEnvelope))
            {
                await QueueBatchAsync(
                    session, myNodeId, projectId, myOwnerFingerprint, requesterNodeId, request.RequestId, batch, now,
                    cancellationToken);
                envelopesQueued++;
                batch = [];
                batchBytes = 0;
            }

            batch.Add(record);
            batchBytes += recordJson.Length;
        }

        if (batch.Count > 0)
        {
            await QueueBatchAsync(
                session, myNodeId, projectId, myOwnerFingerprint, requesterNodeId, request.RequestId, batch, now,
                cancellationToken);
            envelopesQueued++;
        }

        return envelopesQueued;
    }

    /// <summary>
    /// Which streams an ask for one named stream actually serves: that stream, plus every run
    /// stream this node holds for it when the stream is a task's (task 9eb5b245). A run's own
    /// stream id is not derivable from the task's — the join is
    /// <see cref="RunListItem.TaskId"/> — so a task pull answered with the task stream alone lands
    /// a task whose runs are nowhere: observed 2026-09-21, when the Mac pulled 3727884f and it
    /// landed Delivered with laps 0 and sessions 0 because the answer carried the task stream and
    /// nothing else, while the answering node had it Done.
    /// <para>
    /// Each run is served WHOLE and genesis first, which needs no work of its own here: the caller's
    /// single scan is ordered by this node's own global sequence, and a run's genesis
    /// (<c>RunDispatched</c>, or <c>RunRecordReconstructed</c>) is by construction the lowest
    /// sequence on its stream.
    /// </para>
    /// <para>
    /// A run stream id asked for directly answers as itself: the join finds no run whose TaskId is a
    /// run id, so the list is the one stream, which is exactly what <c>h9k task pull &lt;run-id&gt;</c>
    /// means.
    /// </para>
    /// </summary>
    private static async Task<List<Guid>> ResolveRequestedStreamIdsAsync(
        IQuerySession session, Guid forStreamId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(run => run.TaskId == forStreamId)
            .ToListAsync(cancellationToken);
        return StreamIdsToServe(forStreamId, runs.Select(run => run.Id));
    }

    /// <summary>The composition <see cref="ResolveRequestedStreamIdsAsync"/> applies to what it
    /// read, kept separate from the read so the rule itself is checkable without a database: the
    /// asked-for stream first, then each of its runs once.</summary>
    internal static List<Guid> StreamIdsToServe(Guid forStreamId, IEnumerable<Guid> runStreamIds) =>
        [forStreamId, .. runStreamIds.Where(runId => runId != forStreamId).Distinct()];

    /// <summary>
    /// One answering envelope, addressed to the requester alone and stamped with the id of the
    /// request it answers. That id is what lets the requester's own <c>EventReplicationInbox</c>
    /// tell THIS request's answer apart from a sibling catch-up answer arriving in the same read: a
    /// whole-project history pull names no stream to match and its answer may apply nothing at all,
    /// so "some node-addressed answer went by" was the only signal it had, and any other
    /// outstanding request's answer closed it early (independent pre-PR review, cycle 4,
    /// conformance lens, low). Carried in the envelope's own <c>about</c> field rather than in the
    /// batch body, which is a bare JSON array on the wire and cannot gain a field without breaking
    /// every build already decoding it; an events envelope is never stored as an ordinary
    /// <c>MessageDetails</c> on the receiving side, so nothing else here reads <c>about</c>.
    /// </summary>
    private static Task QueueBatchAsync(
        IDocumentSession session, Guid myNodeId, Guid projectId, string myOwnerFingerprint, Guid requesterNodeId,
        Guid requestId, List<EventReplicationCodec.ReplicatedEventRecord> batch, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MessageOutbox.QueueAsync(
            session, myNodeId, projectId, myOwnerFingerprint, MessageAudience.Node(requesterNodeId),
            about: requestId.ToString(),
            MessageKind.Events, EventReplicationCodec.EncodeBatch(batch), now, cancellationToken);

    /// <summary>
    /// idea 8c5993c5: <paramref name="requesterNodeId"/>'s own owner root, resolved by reading that
    /// exact node's own self-announced device-key fingerprint (<see cref="NodeSelfAnnouncedKeyResolver"/>
    /// — the identical read <c>GitLedgerMessageTransport.ReadSinceAsync</c> already performs to
    /// authenticate the sender's outbox before this request is ever trusted, so this is a second
    /// lookup against already-trusted content, never a trust decision of its own) and matching it
    /// against <paramref name="trustChain"/>'s own <see cref="TrustChain.OwnerChains"/> via
    /// <see cref="TrustedOwner.ContainsForNode"/>. Matching by fingerprint rather than by scanning
    /// <see cref="TrustedOwner.Nodes"/> for <paramref name="requesterNodeId"/> directly is what
    /// covers an owner's OWN root device, not merely a node it later vouched: <c>h9k project join
    /// --owner</c> establishes <c>owners/&lt;fingerprint&gt;/root.yaml</c> from that device's own key
    /// and never writes a matching <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> entry for
    /// itself (only <c>h9k node vouch</c> writes those, and only for a target OTHER node) — so a
    /// device's own root fingerprint has no <see cref="TrustedNode"/> of its own to scan for,
    /// exactly the gap <see cref="TrustedOwner.ContainsForNode"/>'s own root special case already
    /// exists to close. The prior <c>Nodes.Any(node => node.NodeId == ...)</c> scan never checked
    /// that special case at all, so a fleet-scoped item's own catch-up answer silently withheld
    /// everything from an owner's root node asking its own fleet sibling for it, with no warning and
    /// no distinct <see cref="MessageKind.EventsUnavailable"/> reason (independent pre-PR review,
    /// cycle 3, adversarial lens, medium). Null when no chain was supplied, when the requester has no
    /// well-formed self-announced key on this ledger yet, or when that key traces to no owner chain
    /// this read knows about — a fleet-scoped item's own match then reads false rather than guessing.
    /// </summary>
    private static async Task<string?> ResolveRequesterOwnerRootFingerprintAsync(
        ILedger ledger, string repositoryPath, TrustChain? trustChain, Guid requesterNodeId, CancellationToken cancellationToken)
    {
        if (trustChain is null)
        {
            return null;
        }

        string? requesterFingerprint = await NodeSelfAnnouncedKeyResolver.ResolveFingerprintAsync(
            ledger, repositoryPath, requesterNodeId, cancellationToken);
        if (requesterFingerprint is null)
        {
            return null;
        }

        string requesterNodeIdText = requesterNodeId.ToString();
        foreach (TrustedOwner owner in trustChain.OwnerChains.Values)
        {
            if (owner.ContainsForNode(requesterFingerprint, requesterNodeIdText))
            {
                return owner.RootFingerprint;
            }
        }

        return null;
    }
}
