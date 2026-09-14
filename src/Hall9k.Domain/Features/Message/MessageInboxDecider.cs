using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Message;

public static class MessageInboxDecider
{
    public static InboxCursorAdvanced AdvanceCursor(Guid senderNodeId, long seq, DateTimeOffset at)
    {
        if (seq < 1)
        {
            throw new DomainValidationException("A cursor advances to a real seq, at least 1.");
        }

        return new InboxCursorAdvanced(senderNodeId, seq, at);
    }

    public static InboxSenderIgnored IgnoreSender(Guid senderNodeId, string reason, DateTimeOffset at)
    {
        if (reason.IsBlank())
        {
            throw new DomainValidationException("Ignoring a sender needs the reason it was ignored.");
        }

        return new InboxSenderIgnored(senderNodeId, reason, at);
    }

    /// <summary>A sweep read this sender's outbox successfully but found nothing new to advance the
    /// cursor to — the only way a prior <see cref="InboxSenderIgnored"/> mark still clears.</summary>
    public static InboxSenderVouched ConfirmVouched(Guid senderNodeId, DateTimeOffset at) =>
        new(senderNodeId, at);
}
