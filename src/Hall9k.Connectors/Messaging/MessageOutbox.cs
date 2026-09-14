using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Message;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The sending half of the message seam (idea 202383dc, M1a): allocates the next seq from this
/// node's own store, writes the envelope through <see cref="IMessageTransport"/>, and records the
/// outcome on the message's own stream (<see cref="MessageAggregate"/>, keyed by this node plus the
/// seq it just used). Self-contained — it calls <see cref="IDocumentSession.SaveChangesAsync"/>
/// itself — so the next call's own seq allocation always reads this one's committed result, never
/// an uncommitted one still sitting in the same session.
/// </summary>
public sealed class MessageOutbox(IMessageTransport transport)
{
    public async Task<MessageEnvelopeV1> SendAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid fromNodeId,
        string fromOwnerFingerprint,
        MessageAudience to,
        string? about,
        MessageKind kind,
        string body,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long seq = await NextSeqAsync(session, fromNodeId, cancellationToken);
        MessageEnvelopeV1 envelope = new(seq, now, fromNodeId, fromOwnerFingerprint, to, about, kind, body);
        Guid streamId = MessageStreamId.ForMessage(fromNodeId, seq);

        try
        {
            await transport.SendAsync(
                repositoryPath, fromNodeId, seq, MessageEnvelopeCodec.Encode(envelope), committer, signingKey, cancellationToken);
        }
        catch (Exception exception) when (exception is LedgerPushRejectedException or InvalidOperationException)
        {
            session.Events.StartStream<MessageAggregate>(streamId, MessageDecider.FailSend(fromNodeId, seq, exception.Message, now));
            await session.SaveChangesAsync(cancellationToken);
            throw;
        }

        session.Events.StartStream<MessageAggregate>(streamId, MessageDecider.Send(fromNodeId, seq, now));
        await session.SaveChangesAsync(cancellationToken);
        return envelope;
    }

    /// <summary>Seq is monotonic per node, from this node's own store — never from the ledger,
    /// never from any caller-supplied counter.</summary>
    private static async Task<long> NextSeqAsync(IDocumentSession session, Guid fromNodeId, CancellationToken cancellationToken)
    {
        MessageDetails? highest = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId)
            .OrderByDescending(message => message.Seq)
            .FirstOrDefaultAsync(cancellationToken);
        return (highest?.Seq ?? 0) + 1;
    }
}
