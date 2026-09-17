using System.Globalization;
using System.Text.Json;
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
/// every record already stored and applies nothing a second time. Each applied event is appended,
/// as-is, to the local stream <see cref="EventReplicationCodec.ReplicatedEventRecord.StreamId"/>
/// names (starting the stream when it does not exist yet), with no decider in the way — it is a
/// fact this node is recording happened elsewhere, not a decision this node is making.
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

        object? data;
        try
        {
            data = JsonSerializer.Deserialize(record.EventDataJson, eventType, JsonOptions);
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

        bool streamExists = streamsStartedThisRead.Contains(record.StreamId)
            || await session.Events.FetchStreamStateAsync(record.StreamId, cancellationToken) is not null;
        StreamAction action = streamExists
            ? session.Events.Append(record.StreamId, data)
            : session.Events.StartStream(record.StreamId, data);
        streamsStartedThisRead.Add(record.StreamId);

        IEvent appended = action.Events[^1];
        appended.SetHeader(ReplicationEventHeaders.OriginNodeId, record.OriginNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, record.OriginOwnerRootFingerprint);
        appended.SetHeader(ReplicationEventHeaders.OriginEventId, record.OriginEventId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginSequence, record.OriginSequence.ToString(CultureInfo.InvariantCulture));
        appended.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, senderNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedAt, now.ToString("O"));

        session.Store(new ReplicatedEventRecord
        {
            Id = record.OriginEventId,
            StreamId = record.StreamId,
            ProjectId = projectId,
            AppliedAt = now,
        });

        return true;
    }
}
