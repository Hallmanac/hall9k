using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events-request</c>/<c>events-unavailable</c>
/// envelopes for one project.</summary>
public sealed record EventCatchUpInboxReadResult(int RequestsAnswered, int DeclinesObserved);

/// <summary>
/// The receiving half of the catch-up protocol (idea 202383dc, M2b, task 9408d525): reads
/// <paramref name="senderNodeId"/>'s outbox the same way <c>EventReplicationInbox</c> does — same
/// transport, same chain verification, same per-sender gap-stop rule — but keeps its own cursor
/// (<see cref="EventCatchUpInboxCursor"/>) and looks only at
/// <see cref="MessageKind.EventsRequest"/>/<see cref="MessageKind.EventsUnavailable"/> envelopes,
/// answering a request addressed to this node via <see cref="EventCatchUpResponder"/> and marking an
/// outstanding request declined when its current candidate says it cannot answer. Kept independent
/// of <c>EventReplicationInbox</c> and <c>MessageInbox</c> for the identical reason those two are
/// independent of each other: a second, narrower reader of the identical outbox ref, at the cost of
/// one extra transport read per moved sender per tick, in exchange for never touching either
/// class's own delicate, heavily-tested flow.
/// </summary>
public sealed class EventCatchUpInbox(
    IMessageTransport transport, EventCatchUpResponder responder, ILogger<EventCatchUpInbox>? logger = null)
{
    public async Task<EventCatchUpInboxReadResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        TrustChain? trustChain,
        CancellationToken cancellationToken)
    {
        Guid cursorId = EventReplicationStreamId.ForCatchUpInboxCursor(senderNodeId, projectId);
        EventCatchUpInboxCursor? cursor = await session.LoadAsync<EventCatchUpInboxCursor>(cursorId, cancellationToken);
        long sinceSeq = cursor?.HighestSeqInspected ?? 0;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);
        if (!read.SenderVouched)
        {
            // A not-vouched sender is already named for h9k status by EventReplicationInbox's own
            // read of the identical outbox — nothing further to record here.
            return new EventCatchUpInboxReadResult(0, 0);
        }

        int answered = 0;
        int declined = 0;
        long highestSeqConsidered = sinceSeq;
        foreach (TransportEnvelope raw in read.Envelopes.OrderBy(envelope => envelope.Seq))
        {
            highestSeqConsidered = raw.Seq;

            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            if (decoded.Outcome != MessageEnvelopeCodec.DecodeOutcome.Parsed)
            {
                continue;
            }

            MessageEnvelopeV1 envelope = decoded.Envelope!;
            if (envelope.Seq != raw.Seq || envelope.FromNode != senderNodeId
                || !envelope.To.Matches(myNodeId, myOwnerFingerprint))
            {
                continue;
            }

            if (envelope.Kind == MessageKind.EventsRequest)
            {
                EventReplicationCodec.EventsRequestRecord? request = EventReplicationCodec.DecodeRequest(envelope.Body);
                if (request is null)
                {
                    logger?.LogWarning(
                        "Events-request envelope {Seq} from sender {SenderNodeId} had a malformed body — skipped",
                        raw.Seq, senderNodeId);
                    continue;
                }

                await responder.AnswerAsync(session, myNodeId, myOwnerFingerprint, projectId, senderNodeId, request, now, cancellationToken);
                answered++;
            }
            else if (envelope.Kind == MessageKind.EventsUnavailable)
            {
                EventReplicationCodec.EventsUnavailableRecord? unavailable = EventReplicationCodec.DecodeUnavailable(envelope.Body);
                if (unavailable is null)
                {
                    continue;
                }

                EventCatchUpRequest? outstanding = await session.LoadAsync<EventCatchUpRequest>(unavailable.RequestId, cancellationToken);
                if (outstanding is { AnsweredAt: null, Exhausted: false } && outstanding.CurrentCandidateNodeId == senderNodeId)
                {
                    outstanding.DeclinedReason = unavailable.Reason;
                    await EventCatchUpCoordinator.AdvanceToNextCandidateAsync(
                        session, myNodeId, myOwnerFingerprint, outstanding, now, cancellationToken);
                    declined++;
                }
            }
        }

        highestSeqConsidered = Math.Max(highestSeqConsidered, read.HighestSeqInspected);
        if (highestSeqConsidered > sinceSeq)
        {
            session.Store(new EventCatchUpInboxCursor
            {
                Id = cursorId,
                SenderNodeId = senderNodeId,
                ProjectId = projectId,
                HighestSeqInspected = highestSeqConsidered,
            });
        }

        await session.SaveChangesAsync(cancellationToken);
        return new EventCatchUpInboxReadResult(answered, declined);
    }
}
