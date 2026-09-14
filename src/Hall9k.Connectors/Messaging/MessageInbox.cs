using Hall9k.Domain.Features.Message;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Messaging;

/// <summary>One sweep's own outcome: how many envelopes this sender's outbox held past this node's
/// cursor, how many actually stored (addressed here and not a duplicate), and whether the sender
/// could not be vouched for at all.</summary>
public sealed record MessageInboxSweepResult(
    Guid SenderNodeId, bool SenderIgnored, int EnvelopesConsidered, int EnvelopesStored);

/// <summary>
/// The receiving half of the message seam (idea 202383dc, M1a): reads everything
/// <c>senderNodeId</c>'s outbox holds since this node's own cursor for that sender, stores only
/// what is addressed to this node, its owner, or the project, and always advances the cursor to the
/// highest seq this sweep actually looked at — whether or not it was stored — so an envelope for
/// someone else, an unrecognized kind, or an unsupported version is never re-fetched forever.
/// Self-contained, the same reason <see cref="MessageOutbox"/> is: it saves its own session.
/// </summary>
public sealed class MessageInbox(IMessageTransport transport, ILogger<MessageInbox>? logger = null)
{
    /// <param name="sinceSeqOverride">
    /// Reads from an explicit position instead of this node's own persisted cursor for the sender —
    /// a re-fetch (a manual re-sync, a lower bound forced after some outage) rather than this
    /// sweep's ordinary incremental read. The persisted cursor itself only ever advances, never
    /// retreats, and only to a seq this sweep actually saw the transport return: an override below
    /// the cursor can re-deliver an envelope already stored, and the per-message duplicate check
    /// below is exactly what keeps that re-delivery from double-recording it; an override above the
    /// cursor that happens to find nothing new leaves the cursor exactly where it was, rather than
    /// silently skipping ahead over whatever real envelopes might sit between the two.
    /// </param>
    public async Task<MessageInboxSweepResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        long? sinceSeqOverride = null)
    {
        Guid inboxStreamId = MessageStreamId.ForInbox(senderNodeId);
        MessageInboxAggregate? inbox =
            await session.Events.AggregateStreamAsync<MessageInboxAggregate>(inboxStreamId, token: cancellationToken);
        long persistedCursor = inbox?.HighestSeqReceived ?? 0;
        long readFrom = sinceSeqOverride ?? persistedCursor;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, readFrom, cancellationToken);
        if (!read.SenderVouched)
        {
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox could not be vouched for by that node's own node file — ignored",
                senderNodeId);
            AppendInboxEvent(
                session, inboxStreamId, inbox is not null,
                MessageInboxDecider.IgnoreSender(senderNodeId, "no node file vouches for this sender's outbox", now));
            await session.SaveChangesAsync(cancellationToken);
            return new MessageInboxSweepResult(senderNodeId, SenderIgnored: true, EnvelopesConsidered: 0, EnvelopesStored: 0);
        }

        int stored = 0;
        // Starts at the persisted cursor, never at readFrom: an override above the persisted
        // cursor that happens to find nothing new must never drag the cursor forward with it — that
        // would silently skip every real envelope between the old cursor and the override, forever.
        // The cursor only ever advances to a seq this sweep actually saw the transport return.
        long highestSeqConsidered = persistedCursor;
        foreach (TransportEnvelope raw in read.Envelopes.OrderBy(envelope => envelope.Seq))
        {
            highestSeqConsidered = raw.Seq;

            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            if (decoded.Outcome != MessageEnvelopeCodec.DecodeOutcome.Parsed)
            {
                logger?.LogWarning(
                    "Envelope {Seq} from sender {SenderNodeId} carries an unsupported version {Version} — refused",
                    raw.Seq, senderNodeId, decoded.Version);
                continue;
            }

            MessageEnvelopeV1 envelope = decoded.Envelope!;
            if (!envelope.To.Matches(myNodeId, myOwnerFingerprint))
            {
                continue;
            }

            Guid messageStreamId = MessageStreamId.ForMessage(senderNodeId, envelope.Seq);
            MessageAggregate? message =
                await session.Events.AggregateStreamAsync<MessageAggregate>(messageStreamId, token: cancellationToken);
            if (message?.ReceivedAt is not null)
            {
                // Already recorded — a re-fetch (a reset cursor, an overlapping read) never
                // double-records the same (sender, seq) message.
                continue;
            }

            MessageReceived receivedEvent = MessageDecider.Receive(senderNodeId, envelope, now);
            if (message is null)
            {
                session.Events.StartStream<MessageAggregate>(messageStreamId, receivedEvent);
            }
            else
            {
                session.Events.Append(messageStreamId, receivedEvent);
            }

            stored++;
        }

        if (highestSeqConsidered > persistedCursor)
        {
            AppendInboxEvent(
                session, inboxStreamId, inbox is not null,
                MessageInboxDecider.AdvanceCursor(senderNodeId, highestSeqConsidered, now));
        }

        await session.SaveChangesAsync(cancellationToken);
        return new MessageInboxSweepResult(senderNodeId, SenderIgnored: false, read.Envelopes.Count, stored);
    }

    private static void AppendInboxEvent(IDocumentSession session, Guid streamId, bool streamExists, object @event)
    {
        if (streamExists)
        {
            session.Events.Append(streamId, @event);
        }
        else
        {
            session.Events.StartStream<MessageInboxAggregate>(streamId, @event);
        }
    }
}
