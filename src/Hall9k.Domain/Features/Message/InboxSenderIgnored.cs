namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This sender is ignored for one of two different reasons, told apart by
/// <see cref="VerificationFailed"/>: either the sender's own outbox commit could not be vouched
/// for at all by the key in that node's node file (idea 202383dc's sender-verification rule,
/// <see cref="VerificationFailed"/> false), or the node file vouches for the sender but a specific
/// envelope from it failed signature verification (<see cref="VerificationFailed"/> true) — a
/// forged or corrupted envelope, not an absent node file. <c>h9k status</c> (M1b) names the sender
/// from this, and only a genuine cursor advance past the same range ever clears the
/// <see cref="VerificationFailed"/> case; a sweep that merely finds nothing new never does
/// (<c>Hall9k.Connectors.Messaging.MessageInbox</c>'s own <c>ConfirmVouched</c> logic).
/// </summary>
public sealed record InboxSenderIgnored(Guid SenderNodeId, string Reason, bool VerificationFailed, DateTimeOffset At);
