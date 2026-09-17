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
/// the same commit as any other pending mail. A currently-private task or idea's own events stop
/// this project's own scan at the first one encountered this tick (the same gap-stop idiom
/// <c>GitLedgerMessageTransport.ReadSinceAsync</c> already uses for a numeric seq gap): the position
/// never advances past a still-private event, so clearing the flag later always finds it again
/// rather than having silently skipped past it forever.
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

            ReplicationOwnership resolved = await ownership.ResolveAsync(session, candidate, cancellationToken);
            if (resolved.ProjectId != projectId)
            {
                // Belongs to no project this node knows, or a different one — never this project's
                // outbox's business either way, and never worth looking at again.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.IsPrivate)
            {
                // Head-of-line block: stop here so a later clear of the flag finds this event again
                // rather than the position having already advanced past it.
                break;
            }

            EventReplicationCodec.ReplicatedEventRecord record = ToRecord(candidate, nodeId, fromOwnerFingerprint);
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);

            if (batch.Count >= MaxEventsPerEnvelope
                || (batch.Count > 0 && batchBytes + recordJson.Length > MaxBytesPerEnvelope))
            {
                await FlushBatchAsync(session, nodeId, projectId, fromOwnerFingerprint, batch, lastIncludedSequence, now, cancellationToken);
                envelopesQueued++;
                eventsQueued += batch.Count;
                batch = [];
                batchBytes = 0;
            }

            batch.Add(record);
            batchBytes += recordJson.Length;
            lastIncludedSequence = candidate.Sequence;
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(session, nodeId, projectId, fromOwnerFingerprint, batch, lastIncludedSequence, now, cancellationToken);
            envelopesQueued++;
            eventsQueued += batch.Count;
        }
        else if (lastIncludedSequence > sinceSequence)
        {
            session.Store(new EventReplicationOutboxPosition { Id = projectId, LastFlushedGlobalSequence = lastIncludedSequence });
            await session.SaveChangesAsync(cancellationToken);
        }

        return new EventReplicationQueueResult(envelopesQueued, eventsQueued);
    }

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
