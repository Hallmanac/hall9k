using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>How many envelopes one <see cref="EventReplicationOutbox.QueuePendingAsync"/> call
/// actually queued through <see cref="MessageOutbox.QueueAsync"/> — the daemon's own sweep flushes
/// them in the same tick, alongside whatever else this project has pending, through the ordinary
/// <see cref="MessageOutbox.FlushAsync"/> call it already makes.</summary>
public sealed record EventReplicationQueueResult(int EnvelopesQueued, int EventsQueued);

/// <summary>
/// The outbound half of event replication (idea 202383dc, M2a): on this node's own current global
/// event log, past this project's own durable <see cref="EventReplicationOutboxPosition"/> (never
/// before this node's own <see cref="NodeAggregate.ReplicationSwitchOnSequence"/>, recorded here the
/// first time this method ever runs), reads every <see cref="EventScope.ProjectScoped"/> event that
/// belongs to <paramref name="projectId"/>, batches it into envelopes of kind
/// <see cref="MessageKind.Events"/> — capped at <see cref="MaxEventsPerEnvelope"/> events or
/// <see cref="MaxBytesPerEnvelope"/> bytes each — and queues each one through the ordinary
/// <see cref="MessageOutbox.QueueAsync"/>, so the daemon's existing per-project flush lands them in
/// the same commit as any other pending mail. A fact this node itself received by replication
/// (carrying <see cref="ReplicationEventHeaders.OriginEventId"/>) never re-enters this scan: only
/// what this node itself produced travels, exactly as the acceptance criterion says, and re-sending
/// a teammate's own fact back at them under a fresh local event id and this node's own origin stamp
/// would otherwise echo forever, each hop minting a new duplicate on both ends. A currently-private
/// task or idea's own events are skipped, never blocking any other stream's own events in the same
/// scan — but the durable position this project's own next scan resumes from never advances past
/// the earliest one still found private this tick (the same gap-stop idiom
/// <c>GitLedgerMessageTransport.ReadSinceAsync</c> already uses for a numeric seq gap), so clearing
/// the flag later always finds it again rather than having silently skipped past it forever; every
/// non-private event already queued past that point simply gets rescanned, and re-batched, every
/// tick until the flag clears — wasteful, never wrong, since a receiver dedupes by origin event id.
/// </summary>
public sealed class EventReplicationOutbox(ReplicationProjectResolver ownership)
{
    public const int MaxEventsPerEnvelope = 200;
    public const int MaxBytesPerEnvelope = 250 * 1024;

    public async Task<EventReplicationQueueResult> QueuePendingAsync(
        IDocumentSession session,
        Guid nodeId,
        Guid projectId,
        string fromOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long switchOnSequence = await EnsureSwitchedOnAsync(session, nodeId, now, cancellationToken);

        EventReplicationOutboxPosition? position =
            await session.LoadAsync<EventReplicationOutboxPosition>(projectId, cancellationToken);
        long sinceSequence = Math.Max(position?.LastFlushedGlobalSequence ?? 0, switchOnSequence);

        IReadOnlyList<IEvent> candidates = await session.Events.QueryAllRawEvents()
            .Where(e => e.Sequence > sinceSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return new EventReplicationQueueResult(0, 0);
        }

        List<EventReplicationCodec.ReplicatedEventRecord> batch = [];
        long batchBytes = 0;
        long lastIncludedSequence = sinceSequence;
        // The sequence of the first still-private stream's own event this scan encountered, or
        // null while none has. Once set it never changes — candidates arrive in increasing
        // sequence order, so the first one found is already the earliest possible — and it caps
        // every position this call persists, so a later scan always re-finds it once the flag
        // clears rather than having already scanned past it.
        long? heldBackAtSequence = null;
        int envelopesQueued = 0;
        int eventsQueued = 0;

        foreach (IEvent candidate in candidates)
        {
            EventScope scope;
            try
            {
                scope = EventScopeRegistry.ClassificationOf(candidate.EventType);
            }
            catch (InvalidOperationException)
            {
                // Never classified — cannot decide whether this is replication's business at all.
                // Skipping it here is the fail-soft twin of EventScopeRegistryTests' own build-time
                // gate, which is what actually stops an unclassified type from shipping.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (scope != EventScope.ProjectScoped)
            {
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (candidate.GetHeader(ReplicationEventHeaders.OriginEventId) is not null)
            {
                // Already a fact this node received by replication, not one it produced itself —
                // sending it back out would echo a teammate's own event back at them under a fresh
                // local event id and this node's origin stamp, forever. Only what this node itself
                // wrote travels.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            ReplicationOwnership resolved = await ownership.ResolveAsync(session, candidate, cancellationToken);
            if (resolved.ProjectId != projectId)
            {
                // Belongs to no project this node knows, or a different one — never this project's
                // outbox's business either way, and never worth looking at again.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.IsProjectStreamItself)
            {
                // The Project aggregate's own stream id is never shared across installs (each node
                // mints its own at registration) — a fact appended under it could never land on a
                // receiver's own Project stream, only create a phantom one under a foreign id. Never
                // worth looking at again, the same as any other never-this-project's-business skip.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.IsPrivate)
            {
                // Skip only this event, never the rest of the scan: a private task or idea must not
                // hold back every other stream's own events. The position write below still never
                // advances past heldBackAtSequence, so this one is found again once the flag clears.
                heldBackAtSequence ??= candidate.Sequence;
                continue;
            }

            EventReplicationCodec.ReplicatedEventRecord record = ToRecord(candidate, nodeId, fromOwnerFingerprint);
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);

            if (batch.Count >= MaxEventsPerEnvelope
                || (batch.Count > 0 && batchBytes + recordJson.Length > MaxBytesPerEnvelope))
            {
                await FlushBatchAsync(
                    session, nodeId, projectId, fromOwnerFingerprint, batch,
                    CapAtHeldBackPosition(lastIncludedSequence, heldBackAtSequence), now, cancellationToken);
                envelopesQueued++;
                eventsQueued += batch.Count;
                batch = [];
                batchBytes = 0;
            }

            batch.Add(record);
            batchBytes += recordJson.Length;
            lastIncludedSequence = candidate.Sequence;
        }

        long finalPosition = CapAtHeldBackPosition(lastIncludedSequence, heldBackAtSequence);

        if (batch.Count > 0)
        {
            await FlushBatchAsync(session, nodeId, projectId, fromOwnerFingerprint, batch, finalPosition, now, cancellationToken);
            envelopesQueued++;
            eventsQueued += batch.Count;
        }
        else if (finalPosition > sinceSequence)
        {
            session.Store(new EventReplicationOutboxPosition { Id = projectId, LastFlushedGlobalSequence = finalPosition });
            await session.SaveChangesAsync(cancellationToken);
        }

        return new EventReplicationQueueResult(envelopesQueued, eventsQueued);
    }

    /// <summary>Never lets a persisted position pass the earliest still-private event this scan
    /// found, however far past it later, eligible events were actually queued and sent.</summary>
    private static long CapAtHeldBackPosition(long candidatePosition, long? heldBackAtSequence) =>
        heldBackAtSequence is { } heldBack ? Math.Min(candidatePosition, heldBack - 1) : candidatePosition;

    private static async Task FlushBatchAsync(
        IDocumentSession session, Guid nodeId, Guid projectId, string fromOwnerFingerprint,
        List<EventReplicationCodec.ReplicatedEventRecord> batch, long positionAfterBatch, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Stored in the same session as the QueueAsync call below, which saves it: the position
        // advance and the queued envelope land in one commit, so a crash between the two can never
        // happen (idea 202383dc: "a restart never re-sends or skips").
        session.Store(new EventReplicationOutboxPosition { Id = projectId, LastFlushedGlobalSequence = positionAfterBatch });

        string body = EventReplicationCodec.EncodeBatch(batch);
        await MessageOutbox.QueueAsync(
            session, nodeId, projectId, fromOwnerFingerprint, MessageAudience.Project, about: null,
            MessageKind.Events, body, now, cancellationToken);
    }

    private static EventReplicationCodec.ReplicatedEventRecord ToRecord(IEvent candidate, Guid nodeId, string fromOwnerFingerprint)
    {
        string originNodeIdText = candidate.GetHeader(EventOriginStampingListener.NodeIdHeader) as string ?? string.Empty;
        Guid originNodeId = Guid.TryParse(originNodeIdText, out Guid parsedNodeId) ? parsedNodeId : nodeId;
        string originOwnerRootFingerprint =
            candidate.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string ?? fromOwnerFingerprint;

        return new EventReplicationCodec.ReplicatedEventRecord(
            candidate.StreamId,
            candidate.EventType.FullName ?? candidate.EventType.Name,
            System.Text.Json.JsonSerializer.Serialize(candidate.Data, candidate.EventType),
            candidate.Id,
            candidate.Sequence,
            originNodeId,
            originOwnerRootFingerprint,
            candidate.Timestamp);
    }

    /// <summary>The first time this ever runs on this node, records the node's own current global
    /// sequence as its switch-on point (idea 202383dc: "the first time replication runs on a node it
    /// records that node's current global sequence as its switch-on point on the Node stream; events
    /// before it never travel"). Idempotent: a node that has already switched on just replays its
    /// recorded point back.</summary>
    private static async Task<long> EnsureSwitchedOnAsync(
        IDocumentSession session, Guid nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NodeAggregate node = await session.Events.AggregateStreamAsync<NodeAggregate>(nodeId, token: cancellationToken)
            ?? throw new InvalidOperationException($"Node {nodeId} has no stream to switch replication on for.");

        if (node.ReplicationSwitchOnSequence is { } existing)
        {
            return existing;
        }

        IReadOnlyList<IEvent> latest = await session.Events.QueryAllRawEvents()
            .OrderByDescending(e => e.Sequence)
            .Take(1)
            .ToListAsync(cancellationToken);
        long currentGlobalSequence = latest.Count > 0 ? latest[0].Sequence : 0;

        session.Events.Append(nodeId, NodeDecider.SwitchOnReplication(node, currentGlobalSequence, now));
        await session.SaveChangesAsync(cancellationToken);
        return currentGlobalSequence;
    }
}
