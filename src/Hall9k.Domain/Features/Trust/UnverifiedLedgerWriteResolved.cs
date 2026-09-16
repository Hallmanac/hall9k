namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// A sweep whose trust chain read no longer names this stream's own writer among
/// <c>TrustChain.UnverifiedWrites</c> — the write that raised <see cref="UnverifiedLedgerWriteObserved"/>
/// became verifiable again (the offending node was re-vouched, or a later commit corrected the
/// bad write) and <c>h9k status</c> stops naming it. The sibling mechanism for the envelope half of
/// the same acceptance criterion already clears itself this way
/// (<see cref="Hall9k.Domain.Features.Message.InboxSenderVouched"/>); this is the ledger-write
/// half's own equivalent, so a resolved standing warning does not outlive the condition that raised
/// it.
/// </summary>
public sealed record UnverifiedLedgerWriteResolved(DateTimeOffset At);
