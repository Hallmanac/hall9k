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
    /// <summary>A push can land at the ledger and still never be reflected in this node's own
    /// local store — the process dies, or the token is cancelled, between the two — so the very
    /// next seq this store's own query allocates can already be occupied. Bounded rather than
    /// infinite: a genuine allocation lag self-heals in one bump, and running past that many means
    /// something else is wrong that retrying alone will not fix — <see cref="SendAsync"/> lets the
    /// conflict propagate once this bound is hit rather than recording a failure against a seq
    /// whose real content belongs to someone else entirely.</summary>
    private const int MaxSeqAdvancesOnConflict = 5;

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
        for (int attempt = 0; ; attempt++)
        {
            MessageEnvelopeV1 envelope = new(seq, now, fromNodeId, fromOwnerFingerprint, to, about, kind, body);
            Guid streamId = MessageStreamId.ForMessage(fromNodeId, seq);

            try
            {
                await transport.SendAsync(
                    repositoryPath, fromNodeId, seq, MessageEnvelopeCodec.Encode(envelope), committer, signingKey, cancellationToken);
            }
            catch (MessageSeqAlreadyUsedException) when (attempt < MaxSeqAdvancesOnConflict)
            {
                // This node's own local store lagged an earlier push that already landed at this
                // seq — the ledger already holds different, real content there, so recording this
                // new message as a failure at that same seq would misattribute it. Move to the
                // next seq instead; NextSeqAsync's own next call will see whichever of the two
                // this session actually commits.
                seq++;
                continue;
            }
            // MessageSeqAlreadyUsedException is itself an InvalidOperationException, so it must be
            // excluded here explicitly — once the catch above stops retrying (MaxSeqAdvancesOnConflict
            // exhausted), a conflict at yet another seq must never fall through to this one: the
            // ledger already holds different, real content at that seq, and recording *this*
            // message's own content as a failure there would misattribute it exactly the same way
            // the retry above exists to prevent. Nothing safe to record here — every seq this
            // attempt ever tried belongs to someone else's real content — so it propagates instead.
            catch (Exception exception)
                when (exception is LedgerPushRejectedException
                    || (exception is InvalidOperationException and not MessageSeqAlreadyUsedException))
            {
                session.Events.StartStream<MessageAggregate>(streamId, MessageDecider.FailSend(envelope, exception.Message, now));
                await session.SaveChangesAsync(cancellationToken);
                throw;
            }

            session.Events.StartStream<MessageAggregate>(streamId, MessageDecider.Send(fromNodeId, seq, now));
            await session.SaveChangesAsync(cancellationToken);
            return envelope;
        }
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
