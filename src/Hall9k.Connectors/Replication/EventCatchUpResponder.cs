using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Persistence;
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
/// named global sequence bound). Task a56cf16e, Decisions Log #PLACEHOLDER-a56cf16e: history is
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
        CancellationToken cancellationToken)
    {
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

            if (resolved.IsPrivate)
            {
                // A currently-private task or idea's own events never ride the ordinary outbox
                // flush (EventReplicationOutbox.QueuePendingAsync) — a catch-up answer must honor
                // the identical hold-back, never forwarding a draft a teammate should not see yet
                // just because this node happens to hold a copy of it (independent pre-PR review,
                // cycle 1, both lenses, high).
                continue;
            }

            (Guid originNodeId, string originOwnerRootFingerprint, Guid originEventId, long originSequence) =
                ResolveOrigin(candidate, myNodeId, myOwnerFingerprint);

            if (originNodeId == requesterNodeId)
            {
                // Never hand a node its own history back: the requester's own dedupe
                // (EventReplicationInbox.ApplyAsync) only recognises an event it received BY
                // REPLICATION, never one it produced natively, so an echoed-back event of its own
                // would apply as a second, un-deduped copy onto its own stream (independent pre-PR
                // review, cycle 1, conformance lens, high).
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
    /// This event's true origin: the preserved <see cref="ReplicationEventHeaders"/> when this is
    /// itself a fact this node received by replication (never this node's own local event id or
    /// sequence, which would silently overwrite the real origin the moment this node forwards it a
    /// second hop), falling back to this node's own stamped identity
    /// (<see cref="EventOriginStampingListener"/>) for an event this node genuinely produced —
    /// the identical technique <see cref="EventReplicationOutbox.ToRecord"/> already uses for the
    /// own-produced half alone. <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginAt"/>
    /// is always this node's own local <see cref="IEvent.Timestamp"/> for the applied copy — a
    /// replicated event's true original timestamp is never itself preserved as a header, only its
    /// identity — which is a documented, harmless approximation: nothing dedupes or orders on it.
    /// </summary>
    private static (Guid OriginNodeId, string OriginOwnerRootFingerprint, Guid OriginEventId, long OriginSequence) ResolveOrigin(
        IEvent candidate, Guid myNodeId, string myOwnerFingerprint)
    {
        string? originNodeIdText = candidate.GetHeader(ReplicationEventHeaders.OriginNodeId) as string;
        if (originNodeIdText.IsNotBlank() && Guid.TryParse(originNodeIdText, out Guid originNodeId))
        {
            string originOwnerRootFingerprint =
                candidate.GetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint) as string ?? myOwnerFingerprint;
            Guid originEventId = Guid.TryParse(
                candidate.GetHeader(ReplicationEventHeaders.OriginEventId) as string, out Guid parsedEventId)
                ? parsedEventId
                : candidate.Id;
            long originSequence = long.TryParse(
                candidate.GetHeader(ReplicationEventHeaders.OriginSequence) as string, out long parsedSequence)
                ? parsedSequence
                : candidate.Sequence;
            return (originNodeId, originOwnerRootFingerprint, originEventId, originSequence);
        }

        string ownNodeIdText = candidate.GetHeader(EventOriginStampingListener.NodeIdHeader) as string ?? string.Empty;
        Guid ownNodeId = Guid.TryParse(ownNodeIdText, out Guid parsedOwnNodeId) ? parsedOwnNodeId : myNodeId;
        string ownOwnerRootFingerprint =
            candidate.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string ?? myOwnerFingerprint;
        return (ownNodeId, ownOwnerRootFingerprint, candidate.Id, candidate.Sequence);
    }
}
