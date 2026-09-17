using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events</c>-kind envelopes for one
/// project.</summary>
public sealed record EventReplicationReadResult(bool SenderIgnored, int EventsApplied);

/// <summary>
/// The inbound half of event replication (idea 202383dc, M2a): reads <paramref name="senderNodeId"/>'s
/// outbox the same way <c>MessageInbox.ReadFromAsync</c> does — same transport, same chain
/// verification, same per-sender gap-stop rule — but keeps its own cursor
/// (<see cref="EventReplicationInboxCursor"/>) and looks only at <see cref="MessageKind.Events"/>
/// envelopes, applying each one's own batch rather than recording an ordinary received message:
/// an events envelope never shows up in <c>h9k messages</c>. Kept independent of
/// <c>MessageInbox</c> rather than folded into it, deliberately: this is a second, narrower reader
/// of the identical outbox ref, at the cost of one extra transport read per moved sender per tick,
/// in exchange for never touching that class's own delicate, heavily-tested flow.
/// <para>
/// Idempotent by origin event id (<see cref="ReplicatedEventRecord"/>): a re-delivered batch finds
/// every record already stored and applies nothing a second time. Each applied event is appended to
/// a local stream, starting it when it does not exist yet, with no decider in the way — it is a
/// fact this node is recording happened elsewhere, not a decision this node is making. That stream
/// is <see cref="EventReplicationCodec.ReplicatedEventRecord.StreamId"/> as sent for a Task, Idea,
/// Epic, or Run event (those ids ARE shared across installs), but this node's OWN local Project
/// stream id for one of the Project aggregate's own events — the sender's own id there is a foreign
/// coordinate, never this receiver's (<see cref="ProjectStreamReplicationRules"/>).
/// </para>
/// </summary>
public sealed class EventReplicationInbox(IMessageTransport transport, ILogger<EventReplicationInbox>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EventReplicationReadResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
        DateTimeOffset now,
        TrustChain? trustChain,
        CancellationToken cancellationToken)
    {
        Guid cursorId = EventReplicationStreamId.ForInboxCursor(senderNodeId, projectId);
        EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(cursorId, cancellationToken);
        long sinceSeq = cursor?.HighestSeqInspected ?? 0;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);
        if (!read.SenderVouched)
        {
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox could not be vouched for by that node's own node file — "
                + "its events envelopes are ignored", senderNodeId);
            session.Store(new EventReplicationInboxCursor
            {
                Id = cursorId,
                SenderNodeId = senderNodeId,
                ProjectId = projectId,
                HighestSeqInspected = sinceSeq,
                SenderIgnored = true,
                IgnoredReason = read.NotVouchedReason ?? "no node file vouches for this sender's outbox",
                IgnoredAt = now,
            });
            await session.SaveChangesAsync(cancellationToken);
            return new EventReplicationReadResult(SenderIgnored: true, EventsApplied: 0);
        }

        int applied = 0;
        long highestSeqConsidered = sinceSeq;
        // Tracks every stream this call has already issued a StartStream for, uncommitted:
        // FetchStreamStateAsync only ever sees what is actually saved, so a second record for the
        // same stream later in the identical batch (ordinary for a task with several events in one
        // sweep) would see no stream yet and try to StartStream it again, which Marten refuses as a
        // collision within one session's own pending changes — the exact shape
        // MessageSweepEngine.PersistUnverifiedWritesAsync's own doc already names as a defect this
        // feature must not repeat.
        HashSet<Guid> streamsStartedThisRead = [];
        foreach (TransportEnvelope raw in read.Envelopes.OrderBy(envelope => envelope.Seq))
        {
            highestSeqConsidered = raw.Seq;

            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            if (decoded.Outcome != MessageEnvelopeCodec.DecodeOutcome.Parsed)
            {
                continue;
            }

            MessageEnvelopeV1 envelope = decoded.Envelope!;
            if (envelope.Kind != MessageKind.Events
                || envelope.Seq != raw.Seq
                || envelope.FromNode != senderNodeId)
            {
                continue;
            }

            IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord>? batch = EventReplicationCodec.DecodeBatch(envelope.Body);
            if (batch is null)
            {
                logger?.LogWarning(
                    "Events envelope {Seq} from sender {SenderNodeId} had a malformed batch body — skipped",
                    raw.Seq, senderNodeId);
                continue;
            }

            foreach (EventReplicationCodec.ReplicatedEventRecord record in batch)
            {
                if (await ApplyAsync(session, record, senderNodeId, projectId, streamsStartedThisRead, now, cancellationToken))
                {
                    applied++;
                }
            }
        }

        highestSeqConsidered = Math.Max(highestSeqConsidered, read.HighestSeqInspected);

        session.Store(new EventReplicationInboxCursor
        {
            Id = cursorId,
            SenderNodeId = senderNodeId,
            ProjectId = projectId,
            HighestSeqInspected = highestSeqConsidered,
            SenderIgnored = false,
            IgnoredReason = null,
            IgnoredAt = null,
        });
        await session.SaveChangesAsync(cancellationToken);
        return new EventReplicationReadResult(SenderIgnored: false, applied);
    }

    /// <summary>Returns false, applying nothing, when this origin event id is already stored — the
    /// idempotency a re-delivered batch relies on.</summary>
    private async Task<bool> ApplyAsync(
        IDocumentSession session, EventReplicationCodec.ReplicatedEventRecord record, Guid senderNodeId, Guid projectId,
        HashSet<Guid> streamsStartedThisRead, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await session.LoadAsync<ReplicatedEventRecord>(record.OriginEventId, cancellationToken) is not null)
        {
            return false;
        }

        Type? eventType = ReplicationEventTypeCatalog.Resolve(record.EventTypeName);
        if (eventType is null)
        {
            logger?.LogWarning(
                "Replicated event of type {EventType} (origin {OriginEventId}) is not a type this build "
                + "knows — skipped", record.EventTypeName, record.OriginEventId);
            return false;
        }

        // A well-behaved sender's own outbox already filters to ProjectScoped, never-the-identity-
        // event events (EventReplicationOutbox.QueuePendingAsync) — checked again here so a sender
        // running an older build, or a misbehaving one, can never make this node apply a NodeScoped
        // event (a node-owner claim, this install's own local settings) sight unseen, nor apply
        // ProjectRegistered itself, which mints a foreign project's own id
        // (ProjectStreamReplicationRules.IsProjectIdentityEvent's own doc). Every other project-
        // scoped event on the Project aggregate's own stream IS eligible — its effective stream id
        // (this receiver's own Project stream for the team-facing subset, the sender's own foreign
        // stream id for the per-install lifecycle events) is decided below, rather than excluded
        // here.
        if (EventScopeRegistry.ClassificationOf(eventType) != EventScope.ProjectScoped
            || ProjectStreamReplicationRules.IsProjectIdentityEvent(eventType))
        {
            logger?.LogWarning(
                "Replicated event of type {EventType} (origin {OriginEventId}) is never eligible to travel — skipped",
                record.EventTypeName, record.OriginEventId);
            return false;
        }

        JsonNode? dataNode;
        try
        {
            dataNode = JsonNode.Parse(record.EventDataJson);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(
                exception, "Replicated event {OriginEventId} of type {EventType} failed to deserialize — skipped",
                record.OriginEventId, record.EventTypeName);
            return false;
        }

        // The project id is a per-install coordinate, never this event's shared identity (the
        // ledger repository is that identity) — rewritten here to the local id this inbox is
        // reading for, on whichever field actually carries it, before the event is ever applied.
        if (dataNode is JsonObject dataObject)
        {
            RewriteProjectIdField(dataObject, projectId);
        }

        object? data;
        try
        {
            data = dataNode?.Deserialize(eventType, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(
                exception, "Replicated event {OriginEventId} of type {EventType} failed to deserialize — skipped",
                record.OriginEventId, record.EventTypeName);
            return false;
        }

        if (data is null)
        {
            return false;
        }

        // The Project aggregate's own team-facing events (ProjectTeamSettingsChanged, the
        // membership audit trail) apply to THIS node's own Project stream id, never the sender's —
        // the sender's own id is a foreign coordinate here. Every other project-scoped event (Task,
        // Idea, Epic, Run, and the Project aggregate's own per-install lifecycle events — archive,
        // reactivate, rename, schedule or cancel a purge) keeps its own stream id instead: a
        // Task/Idea/Epic/Run id IS shared across installs, only its own ProjectId field, rewritten
        // above, was ever a per-install coordinate; a lifecycle event's own stream id stays the
        // sender's foreign one deliberately, so a teammate's local archive, rename, or purge
        // decision about THEIR OWN install's copy of the project is recorded as a fact without ever
        // acting on this receiver's own project (independent pre-PR review, cycle 3, conformance
        // lens — ProjectStreamReplicationRules.IsProjectLifecycleEvent's own doc).
        Guid effectiveStreamId = ProjectStreamReplicationRules.IsProjectAggregateStreamEvent(eventType)
            ? projectId
            : record.StreamId;

        bool streamExists = streamsStartedThisRead.Contains(effectiveStreamId)
            || await session.Events.FetchStreamStateAsync(effectiveStreamId, cancellationToken) is not null;
        StreamAction action = streamExists
            ? session.Events.Append(effectiveStreamId, data)
            : session.Events.StartStream(effectiveStreamId, data);
        streamsStartedThisRead.Add(effectiveStreamId);

        IEvent appended = action.Events[^1];
        appended.SetHeader(ReplicationEventHeaders.OriginNodeId, record.OriginNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, record.OriginOwnerRootFingerprint);
        appended.SetHeader(ReplicationEventHeaders.OriginEventId, record.OriginEventId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginSequence, record.OriginSequence.ToString(CultureInfo.InvariantCulture));
        appended.SetHeader(ReplicationEventHeaders.OriginProjectId, record.OriginProjectId.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, senderNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedAt, now.ToString("O"));

        session.Store(new ReplicatedEventRecord
        {
            Id = record.OriginEventId,
            StreamId = effectiveStreamId,
            ProjectId = projectId,
            AppliedAt = now,
        });

        return true;
    }

    /// <summary>Rewrites the top-level <c>projectId</c> property (case-insensitive, matching
    /// whatever casing this build's own JSON options produce) to <paramref name="localProjectId"/>
    /// when the payload carries one — a Task, Idea, or Epic event's own project reference. Leaves
    /// every other field alone, including an "Id" that happens to equal the sender's own project id
    /// on a Project-aggregate event (<see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/>
    /// already rewrites that event's own STREAM id instead, and nothing reads that field back).</summary>
    private static void RewriteProjectIdField(JsonObject dataObject, Guid localProjectId)
    {
        string? matchingKey = dataObject
            .FirstOrDefault(property => string.Equals(property.Key, "projectId", StringComparison.OrdinalIgnoreCase))
            .Key;
        if (matchingKey is not null)
        {
            dataObject[matchingKey] = JsonValue.Create(localProjectId);
        }
    }
}
