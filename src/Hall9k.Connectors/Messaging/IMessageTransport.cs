using Hall9k.Connectors.Ledger;

namespace Hall9k.Connectors.Messaging;

/// <summary>One envelope as the transport itself sees it: its position in the sender's outbox and
/// the raw JSON content a <c>MessageEnvelopeCodec</c> call turns into a real envelope.</summary>
public sealed record TransportEnvelope(long Seq, string Content);

/// <summary>
/// What one <see cref="IMessageTransport.ReadSinceAsync"/> call found. <see cref="SenderVouched"/>
/// false means this sender's own node file could not vouch for the outbox this transport just
/// tried to read — idea 202383dc's sender-verification rule — so <see cref="Envelopes"/> is always
/// empty in that case and nothing from this sender is trusted this sweep.
/// <see cref="HighestSeqInspected"/> is the highest seq this call actually looked at, whether or
/// not it ended up in <see cref="Envelopes"/>: a candidate the transport rejected on its own terms
/// (an invalid signature) still counts, so a reader's cursor can advance past it instead of
/// re-inspecting the identical rejected candidate on every sweep. It never counts a candidate the
/// transport could not even inspect — a numeric gap in the sender's own seq sequence, or a
/// tool-level failure reading the commit that should have introduced it — since neither case is a
/// verdict on an envelope that was actually looked at, and stopping is the only safe choice: the
/// transport cannot tell a forged or corrupted ref apart from this same sender's own earlier failed
/// send that has not been resent yet (seq allocation is this node's own highest-plus-one, but a
/// failed push leaves that seq's slot empty on the ref until something explicitly resends it).
/// <see cref="RejectedSeqs"/> names exactly which candidates were the former — rejected, not merely
/// uninspected — so the reader can log the sender-verification failure rather than the rejection
/// passing through silently. <see cref="StalledAtSeq"/> names the first seq this call could not
/// even inspect, so the caller can log the stall too rather than reading an empty
/// <see cref="Envelopes"/> as "the sender genuinely has nothing new" when it may instead mean
/// "there is more, but this call could not safely reach it yet".
/// </summary>
public sealed record TransportReadResult(
    bool SenderVouched,
    IReadOnlyList<TransportEnvelope> Envelopes,
    long HighestSeqInspected,
    IReadOnlyList<long> RejectedSeqs,
    long? StalledAtSeq = null)
{
    public static readonly TransportReadResult SenderNotVouched = new(false, [], 0, []);

    public static TransportReadResult Ok(
        IReadOnlyList<TransportEnvelope> envelopes, long highestSeqInspected, IReadOnlyList<long>? rejectedSeqs = null,
        long? stalledAtSeq = null) =>
        new(true, envelopes, highestSeqInspected, rejectedSeqs ?? [], stalledAtSeq);
}

/// <summary>
/// The one seam between the message store (<c>Hall9k.Domain.Features.Message</c>) and how an
/// envelope actually travels between nodes (idea 202383dc, M1a). <see cref="GitLedgerMessageTransport"/>
/// is the real implementation, over A1; <see cref="InMemoryMessageTransport"/> stands in for it in
/// every test but A1's own and the chain reader's (Brian's 2026-09-13 testing rule). Nothing outside
/// an implementation of this interface ever touches a <c>refs/hall9k/messages/*</c> ref.
/// </summary>
public interface IMessageTransport
{
    /// <summary>
    /// Writes one envelope's content to <c>messages/&lt;seq&gt;.json</c> in the sender's own
    /// outbox ref. The real implementation's write goes through A1 (<see cref="ILedger.WriteAsync"/>),
    /// signed, one writer per node, never a batch — <paramref name="seq"/> is already allocated by
    /// the caller from this node's own store before this is ever called.
    /// </summary>
    Task SendAsync(
        string repositoryPath,
        Guid fromNodeId,
        long seq,
        string content,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken);

    /// <summary>Every envelope <paramref name="senderNodeId"/>'s outbox holds past
    /// <paramref name="sinceSeq"/>, oldest first — or <see cref="TransportReadResult.SenderNotVouched"/>
    /// when that sender's node file does not vouch for the outbox read.</summary>
    Task<TransportReadResult> ReadSinceAsync(
        string repositoryPath,
        Guid senderNodeId,
        long sinceSeq,
        CancellationToken cancellationToken);
}
