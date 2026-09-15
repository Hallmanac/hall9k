using Hall9k.Domain.Features.Message;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Messaging;

/// <summary>One sweep's own outcome: how many envelopes this sender's outbox held past this node's
/// cursor, how many actually stored (addressed here and not a duplicate), and whether the sender is
/// now ignored — either because it could not be vouched for at all, or because a specific envelope
/// from it failed signature verification even though the sender itself is vouched. <see cref="StalledAtSeq"/>
/// is the first seq this sweep could not inspect at all (a numeric gap in the sender's own outbox, or
/// a transport-level tool failure) — distinct from a rejected candidate, which was inspected and
/// refused; a stalled seq was never reached, so the cursor stops short of it and the same position is
/// retried next sweep.</summary>
public sealed record MessageInboxSweepResult(
    Guid SenderNodeId, bool SenderIgnored, int EnvelopesConsidered, int EnvelopesStored, long? StalledAtSeq = null);

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
    /// retreats, and only to a seq this sweep actually saw the transport return: an override at or
    /// below the cursor can re-deliver an envelope already stored, and the per-message duplicate
    /// check below is exactly what keeps that re-delivery from double-recording it, while the cursor
    /// still advances normally to whatever this sweep actually saw. An override above the cursor
    /// deliberately skips whatever sits between the two without this sweep ever asking the transport
    /// for it, so that gap must never be abandoned: whether the sweep finds nothing past the override
    /// or finds real envelopes beyond it, the cursor is left exactly where it was, rather than
    /// silently skipping ahead over content this sweep never looked at.
    /// </param>
    public async Task<MessageInboxSweepResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        long? sinceSeqOverride = null,
        CancellationToken cancellationToken = default)
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
            if (inbox is null || !inbox.SenderIgnored)
            {
                AppendInboxEvents(
                    session, inboxStreamId, inbox is not null,
                    [MessageInboxDecider.IgnoreSender(senderNodeId, "no node file vouches for this sender's outbox", now)]);
                await session.SaveChangesAsync(cancellationToken);
            }

            return new MessageInboxSweepResult(senderNodeId, SenderIgnored: true, EnvelopesConsidered: 0, EnvelopesStored: 0);
        }

        // An override above the persisted cursor deliberately skips whatever sits between the two
        // without ever asking the transport for it — this sweep must never mistake "found nothing"
        // or "found something past the gap" for permission to drag the cursor across that
        // unexamined range, or the skipped envelopes are lost for good the moment this sweep
        // returns. Only an ordinary sweep (no override, or an override at or below the persisted
        // cursor — a genuine re-fetch) is ever allowed to move the cursor forward.
        bool overrideSkipsAhead = sinceSeqOverride is not null && sinceSeqOverride > persistedCursor;

        foreach (long rejectedSeq in read.RejectedSeqs)
        {
            logger?.LogWarning(
                "Envelope {Seq} from sender {SenderNodeId} failed sender verification — refused",
                rejectedSeq, senderNodeId);
        }

        if (read.StalledAtSeq is { } stalledAtSeq)
        {
            // The transport found seq stalledAtSeq unreachable this sweep — a numeric gap in the
            // sender's own outbox (its own earlier failed send with no resend yet, or forgery or
            // corruption the transport cannot tell apart from here) or a git-tool failure reading
            // it. Either way nothing past it was inspected, so it is never silent: logged here even
            // though nothing today (M1b's sweep and status wiring, not yet built) surfaces it
            // further.
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox has unreachable content at seq {StalledAtSeq} — the cursor "
                + "stays behind it until this resolves",
                senderNodeId, stalledAtSeq);
        }

        int stored = 0;
        // Starts at the persisted cursor, never at readFrom: the cursor only ever advances to a
        // seq this sweep actually saw the transport return, and only when this sweep was not an
        // override that skipped ahead over content it never looked at.
        long highestSeqConsidered = persistedCursor;
        foreach (TransportEnvelope raw in read.Envelopes.OrderBy(envelope => envelope.Seq))
        {
            highestSeqConsidered = raw.Seq;

            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            if (decoded.Outcome != MessageEnvelopeCodec.DecodeOutcome.Parsed)
            {
                logger?.LogWarning(
                    "Envelope {Seq} from sender {SenderNodeId} was refused: {Outcome} (version {Version})",
                    raw.Seq, senderNodeId, decoded.Outcome, decoded.Version);
                continue;
            }

            MessageEnvelopeV1 envelope = decoded.Envelope!;
            if (envelope.Seq != raw.Seq || envelope.FromNode != senderNodeId)
            {
                logger?.LogWarning(
                    "Envelope at transport position {Seq} from sender {SenderNodeId} claims a mismatched "
                    + "identity (seq {ClaimedSeq}, sender {ClaimedSender}) — refused",
                    raw.Seq, senderNodeId, envelope.Seq, envelope.FromNode);
                continue;
            }

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

        // A rejected candidate (a bad signature) was still inspected, so it counts toward the
        // cursor the same as a stored or skipped one — never just the highest seq that happened to
        // parse and route, or a rejected candidate at the tail gets re-inspected on every sweep
        // forever. A stalled seq (read.StalledAtSeq) is the opposite: never inspected at all, so
        // read.HighestSeqInspected already stops short of it and this Math.Max never counts it.
        highestSeqConsidered = Math.Max(highestSeqConsidered, read.HighestSeqInspected);

        bool cursorAdvanced = highestSeqConsidered > persistedCursor && !overrideSkipsAhead;
        bool envelopeVerificationFailed = read.RejectedSeqs.Count > 0;

        List<object> inboxEvents = [];
        if (cursorAdvanced)
        {
            inboxEvents.Add(MessageInboxDecider.AdvanceCursor(senderNodeId, highestSeqConsidered, now));
        }

        if (envelopeVerificationFailed)
        {
            // Appended after any cursor advance above, in the same batch, so its own Apply always
            // wins: a rejected envelope must be named for h9k status even in a sweep whose other,
            // genuinely verified envelopes moved the cursor forward — a cursor advance on its own
            // must never be read as "this sender is fine".
            string reason = read.RejectedSeqs.Count == 1
                ? $"envelope verification failed for seq {read.RejectedSeqs[0]}"
                : $"envelope verification failed for seqs {string.Join(", ", read.RejectedSeqs)}";
            inboxEvents.Add(MessageInboxDecider.IgnoreSender(senderNodeId, reason, now));
        }
        else if (inbox is not null && inbox.SenderIgnored)
        {
            // This sweep read the sender's outbox successfully but found nothing new to advance
            // the cursor to — without this, a prior ignored mark would never clear on its own,
            // even though the sender is vouched again right now.
            inboxEvents.Add(MessageInboxDecider.ConfirmVouched(senderNodeId, now));
        }

        if (inboxEvents.Count > 0)
        {
            AppendInboxEvents(session, inboxStreamId, inbox is not null, inboxEvents);
        }

        await session.SaveChangesAsync(cancellationToken);
        return new MessageInboxSweepResult(
            senderNodeId, envelopeVerificationFailed, read.Envelopes.Count + read.RejectedSeqs.Count, stored,
            read.StalledAtSeq);
    }

    private static void AppendInboxEvents(IDocumentSession session, Guid streamId, bool streamExists, IReadOnlyList<object> events)
    {
        if (streamExists)
        {
            session.Events.Append(streamId, [.. events]);
        }
        else
        {
            session.Events.StartStream<MessageInboxAggregate>(streamId, [.. events]);
        }
    }
}
