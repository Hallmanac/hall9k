using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Message;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>How many envelopes one <see cref="MessageOutbox.FlushAsync"/> call actually landed —
/// zero means there was nothing queued to flush, so the caller pushed no commit at all.</summary>
public sealed record MessageFlushResult(int EnvelopesFlushed);

/// <summary>How many envelopes <see cref="MessageOutbox.SquashAsync"/> kept — a squash never
/// touches the local event store, only the outbox ref's own content, so there is nothing to
/// report beyond the count that survived.</summary>
public sealed record MessageSquashResult(int EnvelopesKept);

/// <summary>
/// The sending half of the message seam (idea 202383dc, M1a; queue-then-flush split M1b):
/// <see cref="QueueAsync"/> allocates the next seq from this node's own store and records the
/// envelope as queued — no transport call, so it never waits on git or a network — and
/// <see cref="FlushAsync"/> is what the daemon's own sweep calls to actually land every envelope
/// queued since the last flush in one push. Both are self-contained — each calls
/// <see cref="IDocumentSession.SaveChangesAsync"/> itself — so <see cref="QueueAsync"/>'s own seq
/// allocation always reads a prior call's committed result, never an uncommitted one still sitting
/// in the same session.
/// </summary>
public sealed class MessageOutbox(IMessageTransport transport)
{
    /// <summary>
    /// Static, unlike every other method here: queueing never touches <see cref="IMessageTransport"/>
    /// at all, so a caller with no transport to hand — <c>h9k message send</c>, which never waits on
    /// git or a network — needs no <see cref="MessageOutbox"/> instance either.
    /// </summary>
    public static async Task<MessageEnvelopeV1> QueueAsync(
        IDocumentSession session,
        Guid fromNodeId,
        string fromOwnerFingerprint,
        MessageAudience to,
        string? about,
        MessageKind kind,
        string body,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long seq = await NextSeqAsync(session, fromNodeId, cancellationToken);
        MessageEnvelopeV1 envelope = new(seq, now, fromNodeId, fromOwnerFingerprint, to, about, kind, body);
        Guid streamId = MessageStreamId.ForMessage(fromNodeId, seq);

        session.Events.StartStream<MessageAggregate>(
            streamId, MessageDecider.Queue(fromNodeId, seq, fromOwnerFingerprint, to, about, kind, body, now));
        await session.SaveChangesAsync(cancellationToken);
        return envelope;
    }

    /// <summary>
    /// Every envelope this node has queued but not yet landed (<c>SentAt is null</c> — true for a
    /// fresh queue and for one whose last flush attempt failed alike) goes into ONE transport call,
    /// which either lands all of them in a single commit or lands none: a push either reaches
    /// origin or it does not, and there is no partial-batch outcome to record. A failed push
    /// re-throws after marking every envelope in the batch failed, so the caller (the daemon's own
    /// sweep) can log it and simply try again next tick — nothing here retries on its own.
    /// </summary>
    public async Task<MessageFlushResult> FlushAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid fromNodeId,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MessageDetails> pending = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId && message.SentAt == null)
            .OrderBy(message => message.Seq)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return new MessageFlushResult(0);
        }

        List<TransportEnvelope> batch = [.. pending.Select(
            message => new TransportEnvelope(message.Seq, MessageEnvelopeCodec.Encode(ToEnvelope(message))))];

        try
        {
            await transport.FlushAsync(repositoryPath, fromNodeId, batch, committer, signingKey, cancellationToken);
        }
        catch (Exception exception) when (exception is LedgerPushRejectedException or InvalidOperationException)
        {
            foreach (MessageDetails message in pending)
            {
                Guid failedStreamId = MessageStreamId.ForMessage(fromNodeId, message.Seq);
                session.Events.Append(failedStreamId, MessageDecider.FailSend(ToEnvelope(message), exception.Message, now));
            }

            await session.SaveChangesAsync(cancellationToken);
            throw;
        }

        foreach (MessageDetails message in pending)
        {
            Guid streamId = MessageStreamId.ForMessage(fromNodeId, message.Seq);
            if (message.SendFailed)
            {
                MessageAggregate aggregate = await session.Events.AggregateStreamAsync<MessageAggregate>(
                    streamId, token: cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Message {fromNodeId}/{message.Seq} has no stream to flush a resend onto.");
                session.Events.Append(streamId, MessageDecider.Resend(aggregate, now));
            }
            else
            {
                session.Events.Append(streamId, MessageDecider.Send(fromNodeId, message.Seq, now));
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new MessageFlushResult(pending.Count);
    }

    /// <summary>
    /// Squashes this node's own outbox to envelopes sent within <paramref name="retention"/>
    /// (idea 202383dc, M1b's retention rule) — reads only <c>SentAt is not null</c> messages
    /// (anything still pending a flush is not physically in the ref yet, so it is never a squash
    /// candidate at all) and rewrites the transport's own copy to hold exactly the survivors.
    /// Touches nothing in the local event store: a message's own history — sent, resent, received,
    /// handled — is a fact this node already recorded, and squashing the outbox never un-happens
    /// it, it only stops re-shipping old bytes over the wire.
    /// <para>
    /// The cutoff is measured against <see cref="MessageDetails.SentAt"/>, never
    /// <see cref="MessageDetails.QueuedAt"/>: a node that could not reach origin for longer than
    /// <paramref name="retention"/> still has every envelope land with a fresh <c>SentAt</c> the
    /// moment its next flush finally succeeds, so measuring from the much older queue time would
    /// force-remove an envelope moments after a reader's very first chance to fetch it
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// Calls <see cref="IMessageTransport.SquashAsync"/> only when at least one sent envelope has
    /// actually aged out: every survivor is already physically present in the transport's own
    /// copy, so a squash with nothing to drop would only force-push a fresh orphan commit under a
    /// new timestamp for no observable change — on a node's fast sweep cadence, that is thousands
    /// of pointless force-pushes a day, and it moves the outbox tip on every tick even for a node
    /// that has never sent anything, which defeats the daemon's own message sweep's unmoved-tip
    /// skip for every reader watching it (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    public async Task<MessageSquashResult> SquashAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid fromNodeId,
        TimeSpan retention,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = now - retention;
        IReadOnlyList<MessageDetails> sent = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId && message.SentAt != null)
            .OrderBy(message => message.Seq)
            .ToListAsync(cancellationToken);

        if (!sent.Any(message => message.SentAt < cutoff))
        {
            return new MessageSquashResult(sent.Count);
        }

        List<MessageDetails> survivors = [.. sent.Where(message => message.SentAt >= cutoff)];
        List<TransportEnvelope> batch = [.. survivors.Select(
            message => new TransportEnvelope(message.Seq, MessageEnvelopeCodec.Encode(ToEnvelope(message))))];
        await transport.SquashAsync(repositoryPath, fromNodeId, batch, committer, signingKey, cancellationToken);
        return new MessageSquashResult(survivors.Count);
    }

    /// <summary>
    /// Rebuilds the exact envelope <see cref="QueueAsync"/> originally queued, from
    /// <see cref="MessageDetails"/>'s own stored fields — every one of them is set by
    /// <see cref="MessageDecider.Queue"/>'s own <see cref="MessageQueued"/> before a candidate ever
    /// reaches <see cref="FlushAsync"/>'s pending query, so a null here means the store itself is
    /// broken, not a case this method should paper over.
    /// </summary>
    private static MessageEnvelopeV1 ToEnvelope(MessageDetails message) => new(
        message.Seq, message.QueuedAt, message.FromNodeId,
        message.FromOwnerFingerprint ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no FromOwnerFingerprint."),
        MessageAudience.Parse(message.To ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no To.")),
        message.About,
        MessageKind.Parse(message.Kind ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no Kind.")),
        message.Body ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no Body."));

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
