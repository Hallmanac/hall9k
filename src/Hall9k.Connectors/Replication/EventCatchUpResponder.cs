using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
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
/// A third exclusion, this node's own replication switch-on point, applies to a gap-fill and a
/// bootstrap but NOT to a request a human explicitly made
/// (<see cref="EventReplicationCodec.EventsRequestRecord.IsExplicitAsk"/>: one named stream, or a
/// named global sequence bound). Task a56cf16e, Decisions Log #236: history is
/// inert until somebody asks for it, and an explicit ask is the opt-in — the switch-on point
/// otherwise made every task published on a
/// node before that node switched replication on permanently unservable to its peers, with the
/// asking side told only "nothing held here matches this request". The two private exclusions above
/// hold regardless of how explicit the ask was.
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
/// </summary>
public sealed class EventCatchUpResponder(ReplicationProjectResolver ownership)
{
    public async Task<int> AnswerAsync(
        IDocumentSession session,
        Guid myNodeId,
        string myOwnerFingerprint,
        Guid projectId,
        Guid requesterNodeId,
        EventReplicationCodec.EventsRequestRecord request,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        TrustChain? trustChain = null)
    {
        // idea 8c5993c5: the requester's own owner root, resolved from the live trust chain — null
        // when none was supplied, or when the requester is not vouched into any owner chain this
        // read knows about, in which case a fleet-scoped item's own match below always reads false
        // (nothing to compare) rather than guessing at a fleet membership nobody has proven.
        string? requesterOwnerRootFingerprint = ResolveRequesterOwnerRootFingerprint(trustChain, requesterNodeId);
        // Bounded to the one requested stream when the request names one (the gap-fill and
        // bootstrap shapes still need the full scan, since neither names a stream up front) —
        // independent pre-PR review, cycle 1, both lenses, low: an unbounded scan of this node's
        // entire event log, on every answered request, was the worst case named there.
        IQueryable<IEvent> query = session.Events.QueryAllRawEvents();
        if (request.ForStreamId is { } queryStreamId)
        {
            query = query.Where(e => e.StreamId == queryStreamId);
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

        // This node's own pre-replication history never travels UNASKED, in a catch-up answer any
        // more than an ordinary outbox flush (EventReplicationOutbox.QueuePendingAsync's own
        // identical Math.Max(position, switchOnSequence) exclusion) — independent pre-PR review,
        // cycle 1, conformance lens, high: an unfiltered scan here would hand a brand-new node this
        // node's entire pre-switch-on back catalogue, which idea 202383dc's migration ruling keeps
        // out of scope for now.
        //
        // An explicit ask (EventsRequestRecord.IsExplicitAsk: one named stream, or a named global
        // sequence bound) is the opt-in that lifts it, and only for that one request — task
        // a56cf16e's own origin incident, 2026-09-19: the Mac's ReplicationSwitchedOn landed at
        // global sequence 30084 while the task the Windows node was asking for sat at 28273-30020,
        // so every explicit stream request for it answered "nothing held here matches this request"
        // and no re-run could ever have changed that. History stays inert until somebody actually
        // asks for it; a human asking IS somebody. Never lifted for the two shapes a daemon sweep
        // mints on its own, an ordinary flush and a gap-fill, which is where the inertness matters.
        long? switchOnSequence = request.IsExplicitAsk
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
                { ForStreamId: { } forStreamId } => candidate.StreamId == forStreamId,
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
    /// idea 8c5993c5: <paramref name="requesterNodeId"/>'s own owner root, read off
    /// <paramref name="trustChain"/>'s own <see cref="TrustChain.OwnerChains"/> — the requester
    /// having been vouched into some owner's chain is already a precondition of reaching this
    /// point (the outbox read that answered THIS request already required
    /// <c>TransportReadResult.SenderVouched</c> for the sender side of the exchange), so this is a
    /// lookup, never a trust decision of its own. Null when no chain was supplied, or when no
    /// chain's own node list names this exact node id — a fleet-scoped item's own match then reads
    /// false rather than guessing.
    /// </summary>
    private static string? ResolveRequesterOwnerRootFingerprint(TrustChain? trustChain, Guid requesterNodeId)
    {
        if (trustChain is null)
        {
            return null;
        }

        string requesterNodeIdText = requesterNodeId.ToString();
        foreach (TrustedOwner owner in trustChain.OwnerChains.Values)
        {
            if (owner.Nodes.Any(node => node.NodeId == requesterNodeIdText))
            {
                return owner.RootFingerprint;
            }
        }

        return null;
    }
}
