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

        IReadOnlyList<IEvent> candidates = await query.ToListAsync(cancellationToken);

        List<EventReplicationCodec.ReplicatedEventRecord> matches = [];
        foreach (IEvent candidate in candidates)
        {
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
                await QueueBatchAsync(session, myNodeId, projectId, myOwnerFingerprint, requesterNodeId, batch, now, cancellationToken);
                envelopesQueued++;
                batch = [];
                batchBytes = 0;
            }

            batch.Add(record);
            batchBytes += recordJson.Length;
        }

        if (batch.Count > 0)
        {
            await QueueBatchAsync(session, myNodeId, projectId, myOwnerFingerprint, requesterNodeId, batch, now, cancellationToken);
            envelopesQueued++;
        }

        return envelopesQueued;
    }

    private static Task QueueBatchAsync(
        IDocumentSession session, Guid myNodeId, Guid projectId, string myOwnerFingerprint, Guid requesterNodeId,
        List<EventReplicationCodec.ReplicatedEventRecord> batch, DateTimeOffset now, CancellationToken cancellationToken) =>
        MessageOutbox.QueueAsync(
            session, myNodeId, projectId, myOwnerFingerprint, MessageAudience.Node(requesterNodeId), about: null,
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
