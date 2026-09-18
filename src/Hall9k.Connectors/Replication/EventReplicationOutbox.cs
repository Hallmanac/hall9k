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
/// the earliest one still owed to a teammate (<see
/// cref="EventReplicationOutboxPosition.PendingPrivateSequences"/>; the same gap-stop idiom
/// <c>GitLedgerMessageTransport.ReadSinceAsync</c> already uses for a numeric seq gap), so clearing
/// the flag later always finds it again rather than having silently skipped past it forever. A
/// separate, never-capped high-water mark
/// (<see cref="EventReplicationOutboxPosition.HighestQueuedSequence"/>) is what stops that resumed
/// rescan from re-queuing an already-sent event past the hold-back point a second time (independent
/// pre-PR review, cycle 3, both lenses: the position alone can only ever move backward while the
/// flag stays set, so every eligible event past it was otherwise re-batched, and re-pushed as a
/// brand-new envelope, on every single sweep for as long as the flag stood) — but the mark alone
/// cannot tell "already sent" apart from "still owed", once some other stream's own later event
/// pushed the mark past a held-back one; <see
/// cref="EventReplicationOutboxPosition.PendingPrivateSequences"/> makes that distinction, so a
/// rescan neither drops a held-back event forever nor pays a full reclassification-and-resolve pass
/// for every already-sent event it re-reads while anything nearby stays private (independent pre-PR
/// review, cycle 5, both lenses).
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
        long highestQueuedSequence = position?.HighestQueuedSequence ?? 0;

        // Sequences this project has found still private and skipped, not yet actually queued —
        // every one of them is re-examined below regardless of where it sits relative to
        // highestQueuedSequence, which is what lets a formerly-private event be told apart from an
        // already-sent one once some other stream's own later event has pushed the mark past it
        // (independent pre-PR review, cycle 5, adversarial lens).
        HashSet<long> pendingPrivate = position?.PendingPrivateSequences is { Count: > 0 } saved ? [.. saved] : [];

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
            if (candidate.Sequence <= highestQueuedSequence && !pendingPrivate.Contains(candidate.Sequence))
            {
                // Already scanned in an earlier sweep and conclusively settled then — queued, or
                // found to be none of this project's business — with nothing left here that could
                // change that reading except its own privacy flag, and a still-private candidate is
                // exactly what pendingPrivate tracks. Skipping the classification and ownership
                // resolution below for the rest is what keeps a long-held-back private draft from
                // making every sweep re-read and re-resolve the whole rest of the project's history
                // past it (independent pre-PR review, cycle 5, conformance lens, medium).
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

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

            if (resolved.IsProjectStreamItself && ProjectStreamReplicationRules.IsProjectIdentityEvent(candidate.EventType))
            {
                // Only the Project aggregate's own identity event (ProjectRegistered) is excluded
                // here: it mints this install's own local project id (ProjectAddCommand), never
                // shared across installs, so a fact appended under it could never land on a
                // receiver's own Project stream, only create a phantom one under a foreign id. Never
                // worth looking at again, the same as any other never-this-project's-business skip.
                // Every OTHER project-scoped event on this same stream still travels like any other
                // candidate below — the receiving inbox rewrites the team-facing subset
                // (ProjectTeamSettingsChanged, MemberVouched, MemberRemoved) onto ITS OWN local
                // Project stream (ProjectStreamReplicationRules.IsProjectAggregateStreamEvent), but
                // keeps the sender's own foreign stream id for the per-install lifecycle events
                // (ProjectStreamReplicationRules.IsProjectLifecycleEvent) — those are this install's
                // own decision about its own local copy, never one a teammate's node may act on
                // (independent pre-PR review, cycle 3, conformance lens).
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.IsPrivate)
            {
                // Skip only this event, never the rest of the scan: a private task or idea must not
                // hold back every other stream's own events. Recorded (or kept recorded) as owed, so
                // a later sweep re-finds it by sequence rather than trusting highestQueuedSequence's
                // reading once the flag clears and some other stream has since pushed the mark past
                // it (the fast skip above, and the position cap below, both key off this set).
                pendingPrivate.Add(candidate.Sequence);
                continue;
            }

            // Reaching here with candidate.Sequence <= highestQueuedSequence is only possible for a
            // pendingPrivate member (the fast skip above is the only thing that could have let it
            // through) — never treated as "already sent" the way the high-water mark alone would
            // read it. It is owed, whichever sweep first held it back.
            EventReplicationCodec.ReplicatedEventRecord record = ToRecord(candidate, nodeId, fromOwnerFingerprint, projectId);
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);

            if (batch.Count >= MaxEventsPerEnvelope
                || (batch.Count > 0 && batchBytes + recordJson.Length > MaxBytesPerEnvelope))
            {
                await FlushBatchAsync(
                    session, nodeId, projectId, fromOwnerFingerprint, batch,
                    CapAtHeldBackPosition(lastIncludedSequence, pendingPrivate), highestQueuedSequence, pendingPrivate,
                    now, cancellationToken);
                envelopesQueued++;
                eventsQueued += batch.Count;
                batch = [];
                batchBytes = 0;
            }

            batch.Add(record);
            batchBytes += recordJson.Length;
            lastIncludedSequence = candidate.Sequence;
            // Math.Max, not a plain assignment: a pendingPrivate member being caught up here can
            // have a LOWER sequence than the mark already reached through other streams, and a plain
            // assignment would regress the mark backward — which would then make the fast skip above
            // wrongly stop trusting every already-sent event between the regressed mark and where it
            // used to be, reclassifying and re-queuing them as duplicates on this very sweep.
            highestQueuedSequence = Math.Max(highestQueuedSequence, candidate.Sequence);
            // Sent now — no longer owed.
            pendingPrivate.Remove(candidate.Sequence);
        }

        long finalPosition = CapAtHeldBackPosition(lastIncludedSequence, pendingPrivate);

        if (batch.Count > 0)
        {
            await FlushBatchAsync(
                session, nodeId, projectId, fromOwnerFingerprint, batch, finalPosition, highestQueuedSequence,
                pendingPrivate, now, cancellationToken);
            envelopesQueued++;
            eventsQueued += batch.Count;
        }
        else if (finalPosition > sinceSequence
            || highestQueuedSequence > (position?.HighestQueuedSequence ?? 0)
            || !pendingPrivate.SetEquals(position?.PendingPrivateSequences ?? []))
        {
            session.Store(new EventReplicationOutboxPosition
            {
                Id = projectId,
                LastFlushedGlobalSequence = finalPosition,
                HighestQueuedSequence = highestQueuedSequence,
                PendingPrivateSequences = [.. pendingPrivate],
            });
            await session.SaveChangesAsync(cancellationToken);
        }

        return new EventReplicationQueueResult(envelopesQueued, eventsQueued);
    }

    /// <summary>Never lets a persisted position pass the earliest event still owed to a teammate,
    /// however far past it later, eligible events were actually queued and sent.</summary>
    private static long CapAtHeldBackPosition(long candidatePosition, HashSet<long> pendingPrivate) =>
        pendingPrivate.Count > 0 ? Math.Min(candidatePosition, pendingPrivate.Min() - 1) : candidatePosition;

    private static async Task FlushBatchAsync(
        IDocumentSession session, Guid nodeId, Guid projectId, string fromOwnerFingerprint,
        List<EventReplicationCodec.ReplicatedEventRecord> batch, long positionAfterBatch, long highestQueuedSequence,
        HashSet<long> pendingPrivate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Stored in the same session as the QueueAsync call below, which saves it: the position
        // advance and the queued envelope land in one commit, so a crash between the two can never
        // happen (idea 202383dc: "a restart never re-sends or skips").
        session.Store(new EventReplicationOutboxPosition
        {
            Id = projectId,
            LastFlushedGlobalSequence = positionAfterBatch,
            HighestQueuedSequence = highestQueuedSequence,
            PendingPrivateSequences = [.. pendingPrivate],
        });

        string body = EventReplicationCodec.EncodeBatch(batch);
        await MessageOutbox.QueueAsync(
            session, nodeId, projectId, fromOwnerFingerprint, MessageAudience.Project, about: null,
            MessageKind.Events, body, now, cancellationToken);
    }

    private static EventReplicationCodec.ReplicatedEventRecord ToRecord(
        IEvent candidate, Guid nodeId, string fromOwnerFingerprint, Guid projectId)
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
            candidate.Timestamp,
            projectId);
    }

    /// <summary>The first time this ever runs on this node, records the node's own current global
    /// sequence as its switch-on point (idea 202383dc: "the first time replication runs on a node it
    /// records that node's current global sequence as its switch-on point on the Node stream; events
    /// before it never travel"). Idempotent: a node that has already switched on just replays its
    /// recorded point back. Internal rather than private: <see cref="EventCatchUpResponder"/> calls
    /// this identical method to learn the same switch-on point before forwarding ANY held event to a
    /// catch-up requester — a pre-switch-on event is this node's own migration-era history, ruled to
    /// never travel (idea 202383dc; Brian's 2026-09-13 ruling), and a catch-up answer must honor that
    /// exclusion exactly as an ordinary outbox flush already does (independent pre-PR review, cycle 1,
    /// conformance lens, high).</summary>
    internal static async Task<long> EnsureSwitchedOnAsync(
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
