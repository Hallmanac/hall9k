using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
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
/// retried next sweep. <see cref="SenderNotVouched"/> is the specific reason <see cref="SenderIgnored"/>
/// can be true with <see cref="EnvelopesConsidered"/> and <see cref="EnvelopesStored"/> both zero — no
/// node file vouches for this sender at all, distinct from a vouched sender whose envelope merely
/// failed signature verification — so a caller deciding whether this read is worth remembering (a
/// probed tip, a cursor) can tell "genuinely read nothing new" from "never actually looked".</summary>
public sealed record MessageInboxSweepResult(
    Guid SenderNodeId, bool SenderIgnored, int EnvelopesConsidered, int EnvelopesStored, long? StalledAtSeq = null,
    bool SenderNotVouched = false);

/// <summary>
/// The receiving half of the message seam (idea 202383dc, M1a): reads everything
/// <c>senderNodeId</c>'s outbox holds since this node's own cursor for that sender IN
/// <paramref name="projectId"/>, stores only what is addressed to this node, its owner, or the
/// project, and always advances that project's own cursor to the highest seq this sweep actually
/// looked at — whether or not it was stored — so an envelope for someone else, an unrecognized
/// kind, or an unsupported version is never re-fetched forever. Self-contained, the same reason
/// <see cref="MessageOutbox"/> is: it saves its own session.
/// <para>
/// The cursor and every message this call stores are scoped to <paramref name="projectId"/> alone
/// (idea 202383dc, M2) — this node's own local project id for whichever repository
/// <paramref name="repositoryPath"/> names. Reading THIS project's own copy of
/// <paramref name="senderNodeId"/>'s outbox never advances, skips, or ignores that same sender's
/// standing in any other project this node also reads it through: each project keeps its own
/// separate cursor stream (<see cref="MessageStreamId.ForInbox"/>) and its own separate message
/// streams (<see cref="MessageStreamId.ForMessage"/>), so a sender common to two projects never
/// collides between them.
/// </para>
/// <para>
/// An <see cref="MessageKind.Events"/> envelope is never stored here (idea 202383dc, M2a): it is
/// <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>'s own business, a second, independent
/// reader of this identical outbox ref on its own cursor — skipped the same way an unrecognized
/// kind is, still counted toward this call's own cursor advance.
/// </para>
/// </summary>
public sealed class MessageInbox(IMessageTransport transport, ILogger<MessageInbox>? logger = null)
{
    /// <param name="projectId">
    /// This node's own local project id for <paramref name="repositoryPath"/> — never the sender's
    /// own local project id (a different install's Guid for what may be the identical shared
    /// project) and never the wire's ledger-derived project key. See this class's own doc.
    /// </param>
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
    /// <param name="trustChain">
    /// Passed straight through to <see cref="IMessageTransport.ReadSinceAsync"/> — when a caller
    /// (<c>MessageSweepEngine.ProbeAndReadAsync</c>) already computed one this same sweep, reusing
    /// it avoids walking the whole ledger chain again for every sender it reads in that same tick.
    /// Null still means "let the transport compute it fresh".
    /// </param>
    public async Task<MessageInboxSweepResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        long? sinceSeqOverride = null,
        TrustChain? trustChain = null,
        CancellationToken cancellationToken = default)
    {
        Guid inboxStreamId = MessageStreamId.ForInbox(senderNodeId, projectId);
        MessageInboxAggregate? inbox =
            await session.Events.AggregateStreamAsync<MessageInboxAggregate>(inboxStreamId, token: cancellationToken);
        long persistedCursor = inbox?.HighestSeqReceived ?? 0;

        // A fresh per-project cursor (inbox is null — this project has never read this sender
        // before under the M2 stream shape) for the one project LegacyMessageAdoption names falls
        // back to the pre-M2 cursor, when one exists, instead of 0: that old, unscoped stream is the
        // one this node actually advanced reading this same sender before this change, through the
        // identical repository the adopting project now owns, so starting over at 0 would re-fetch
        // and re-store every envelope already handled under the old stream ids (independent pre-PR
        // review, cycle 1, conformance lens, medium). Only the adopting project ever takes this
        // fallback — any other eligible project's own first read of this sender is genuinely new,
        // never a continuation of pre-M2 history, and must still start at 0.
        if (inbox is null && await LegacyMessageAdoption.IsAdoptingProjectAsync(session, projectId, cancellationToken))
        {
            Guid legacyInboxStreamId = MessageStreamId.ForInboxBeforeProjectScoping(senderNodeId);
            MessageInboxAggregate? legacyInbox = await session.Events
                .AggregateStreamAsync<MessageInboxAggregate>(legacyInboxStreamId, token: cancellationToken);
            persistedCursor = legacyInbox?.HighestSeqReceived ?? 0;
        }

        long readFrom = sinceSeqOverride ?? persistedCursor;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, readFrom, cancellationToken, trustChain);
        if (!read.SenderVouched)
        {
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox could not be vouched for by that node's own node file — ignored",
                senderNodeId);
            if (inbox is null || !inbox.SenderIgnored)
            {
                // read.NotVouchedReason names the actual cause — no node file at all, or (chain-level,
                // idea 202383dc T1) a node file that exists but whose key the ledger chain itself
                // currently refuses — never the stale, hardcoded M1a reason regardless of which one
                // actually applied (independent pre-PR review, cycle 1, conformance lens, medium).
                string reason = read.NotVouchedReason ?? "no node file vouches for this sender's outbox";
                AppendInboxEvents(
                    session, inboxStreamId, inbox is not null,
                    [MessageInboxDecider.IgnoreSender(senderNodeId, projectId, reason, verificationFailed: false, now)]);
                await session.SaveChangesAsync(cancellationToken);
            }

            return new MessageInboxSweepResult(
                senderNodeId, SenderIgnored: true, EnvelopesConsidered: 0, EnvelopesStored: 0, SenderNotVouched: true);
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

        if (read.PrunedBelowSeq is { } prunedBelowSeq)
        {
            // Unlike the stall logged above, the cursor does NOT stay behind this: a squash's own
            // verified low-water mark already moved it past this range, since the sender itself no
            // longer holds the content — the log exists only so an operator reading it never
            // mistakes silence for "nothing was ever missed" (EventReplicationInbox's own read of
            // the identical outbox is what actually asks a peer for it, via StalledAtSeq).
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox pruned everything below seq {PrunedBelowSeq} in an earlier "
                + "squash — this read resumed at the low-water mark instead of stalling, but that pruned "
                + "range was never inspected by this node",
                senderNodeId, prunedBelowSeq);
        }

        int stored = 0;
        // Starts at the persisted cursor, never at readFrom: the cursor only ever advances to a
        // seq this sweep actually saw the transport return, and only when this sweep was not an
        // override that skipped ahead over content it never looked at.
        long highestSeqConsidered = persistedCursor;
        // This project's own ledger-derived key, resolved once, preferring the live trust chain
        // (idea 202383dc, M2; Brian's ruling 2026-09-17: a project's identity no longer depends on
        // which ledger a message arrived through) over this install's own possibly-stale local
        // mirror, so every envelope below is refused or accepted against the SAME key regardless of
        // how many this sweep inspects.
        string? localProjectKey = await ResolveLocalProjectKeyAsync(session, projectId, trustChain, cancellationToken);
        // Every seq whose own envelope carried a project key that does not match this project's own
        // ledger-derived key above is refused, never stored, and folded into the same standing
        // "sender ignored" record an ordinary signature failure already produces below, so h9k
        // status names it the identical way. A null envelope key, or one that is not shaped like a
        // 26-character ULID (a pre-ruling envelope still carrying the retired owner-fingerprint
        // value, or one from a build older than idea 202383dc's M2), is read as "no opinion" and
        // never refused on that basis alone (MessageEnvelopeV1.ProjectKey's own doc).
        List<long> projectKeyMismatchSeqs = [];
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

            if (envelope.ProjectKey is { Length: 26 } candidateKey
                && await IsProjectKeyMismatchAsync(session, projectId, candidateKey, localProjectKey, cancellationToken))
            {
                projectKeyMismatchSeqs.Add(raw.Seq);
                logger?.LogWarning(
                    "Envelope {Seq} from sender {SenderNodeId} carries a project key that does not match "
                    + "this project's own ledger-derived key, refused", raw.Seq, senderNodeId);
                continue;
            }

            if (!envelope.To.Matches(myNodeId, myOwnerFingerprint))
            {
                continue;
            }

            if (envelope.Kind == MessageKind.Events || envelope.Kind == MessageKind.EventsRequest
                || envelope.Kind == MessageKind.EventsUnavailable)
            {
                // idea 202383dc, M2a/M2b: an events, events-request, or events-unavailable envelope
                // is EventReplicationInbox's/EventCatchUpInbox's own business — a second,
                // independent reader of this identical outbox ref, on its own cursor. It must never
                // also land here as an ordinary received message (h9k messages would otherwise show
                // a raw batch of replicated events, or a catch-up protocol message, as if it were a
                // note); skipping it still lets the cursor above advance past it like any other
                // inspected envelope.
                continue;
            }

            Guid messageStreamId = MessageStreamId.ForMessage(senderNodeId, projectId, envelope.Seq);
            MessageAggregate? message =
                await session.Events.AggregateStreamAsync<MessageAggregate>(messageStreamId, token: cancellationToken);
            if (message?.ReceivedAt is not null)
            {
                // Already recorded — a re-fetch (a reset cursor, an overlapping read) never
                // double-records the same (sender, project, seq) message.
                continue;
            }

            MessageReceived receivedEvent = MessageDecider.Receive(senderNodeId, projectId, envelope, now);
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
        bool envelopeVerificationFailed = read.RejectedSeqs.Count > 0 || projectKeyMismatchSeqs.Count > 0;

        List<object> inboxEvents = [];
        if (cursorAdvanced)
        {
            inboxEvents.Add(MessageInboxDecider.AdvanceCursor(senderNodeId, projectId, highestSeqConsidered, now));
        }

        if (envelopeVerificationFailed)
        {
            // Appended after any cursor advance above, in the same batch, so its own Apply always
            // wins: a rejected envelope must be named for h9k status even in a sweep whose other,
            // genuinely verified envelopes moved the cursor forward — a cursor advance on its own
            // must never be read as "this sender is fine".
            List<string> reasons = [];
            if (read.RejectedSeqs.Count > 0)
            {
                reasons.Add(read.RejectedSeqs.Count == 1
                    ? $"envelope verification failed for seq {read.RejectedSeqs[0]}"
                    : $"envelope verification failed for seqs {string.Join(", ", read.RejectedSeqs)}");
            }

            if (projectKeyMismatchSeqs.Count > 0)
            {
                reasons.Add(projectKeyMismatchSeqs.Count == 1
                    ? $"project key mismatch for seq {projectKeyMismatchSeqs[0]}"
                    : $"project key mismatch for seqs {string.Join(", ", projectKeyMismatchSeqs)}");
            }

            inboxEvents.Add(MessageInboxDecider.IgnoreSender(
                senderNodeId, projectId, string.Join("; ", reasons), verificationFailed: true, now));
        }
        // This sweep read the sender's outbox successfully but found nothing new to advance the
        // cursor to — without this, a prior "not vouched at all" ignored mark would never clear on
        // its own, even though the sender is vouched again right now. Never appended when the
        // standing mark is IgnoredForVerificationFailure: a specific envelope that failed signature
        // verification is a fact about that envelope, not the sender's current vouch status, and a
        // sweep that simply finds nothing new must never be read as clearing it — only a genuine
        // cursor advance past it does (MessageInboxDecider.ConfirmVouched's own doc).
        else if (inbox is not null && inbox.SenderIgnored && !inbox.IgnoredForVerificationFailure)
        {
            inboxEvents.Add(MessageInboxDecider.ConfirmVouched(senderNodeId, projectId, now));
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

    /// <summary>This project's own ledger-derived key: the live trust chain's own value when a
    /// caller actually computed one this tick (every production sweep does, via
    /// <c>MessageSweepEngine.ProbeAndReadAsync</c>), falling back to this install's own local
    /// mirror (<see cref="ProjectDetails.ProjectKey"/>) only when it did not. Null when neither
    /// source has one yet: a project too new, or too far behind, for either to have read one back
    /// from the ledger, in which case a mismatch can never be judged and nothing carrying a project
    /// key is refused on that basis alone.</summary>
    private static async Task<string?> ResolveLocalProjectKeyAsync(
        IDocumentSession session, Guid projectId, TrustChain? trustChain, CancellationToken cancellationToken)
    {
        if (trustChain?.ProjectKey is { } ledgerKey)
        {
            return ledgerKey;
        }

        ProjectDetails? localProject = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return localProject?.ProjectKey;
    }

    /// <summary>Whether a genuinely 26-character <paramref name="candidateKey"/> fails to name this
    /// project: a direct mismatch against <paramref name="localProjectKey"/> when this install
    /// already knows it, or (the only case that needs a lookup at all, since a known key already
    /// answers the question directly) a hit against some OTHER local project's own recorded key
    /// when it does not, so a project too new or too far behind to have read its own key back yet
    /// still refuses an envelope this node can already prove belongs elsewhere.</summary>
    private static async Task<bool> IsProjectKeyMismatchAsync(
        IDocumentSession session, Guid projectId, string candidateKey, string? localProjectKey, CancellationToken cancellationToken)
    {
        if (localProjectKey is not null)
        {
            return candidateKey != localProjectKey;
        }

        ProjectDetails? resolvedByKey = await session.Query<ProjectDetails>()
            .Where(candidate => candidate.ProjectKey == candidateKey)
            .FirstOrDefaultAsync(cancellationToken);
        return resolvedByKey is not null && resolvedByKey.Id != projectId;
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
