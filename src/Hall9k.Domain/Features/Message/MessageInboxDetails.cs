using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Message;

/// <summary>This node's own read view of one sender's inbox — never in the repository, per idea
/// 202383dc's own rule that cursors are local. <c>h9k status</c> (M1b) reads
/// <see cref="SenderIgnored"/> to name a sender it cannot currently trust — either because no node
/// file vouches for it at all, or because <see cref="IgnoredForVerificationFailure"/> is true and a
/// specific envelope from an otherwise-vouched sender failed signature verification.</summary>
public sealed class MessageInboxDetails
{
    public Guid Id { get; set; }
    public Guid SenderNodeId { get; set; }
    public long HighestSeqReceived { get; set; }
    public bool SenderIgnored { get; set; }
    public string? IgnoredReason { get; set; }
    public bool IgnoredForVerificationFailure { get; set; }
    public DateTimeOffset? IgnoredAt { get; set; }
}

public sealed class MessageInboxDetailsProjection : SingleStreamProjection<MessageInboxDetails, Guid>
{
    public MessageInboxDetails Create(IEvent<InboxCursorAdvanced> @event) => new()
    {
        Id = @event.StreamId,
        SenderNodeId = @event.Data.SenderNodeId,
        HighestSeqReceived = @event.Data.Seq,
    };

    public MessageInboxDetails Create(IEvent<InboxSenderIgnored> @event) => new()
    {
        Id = @event.StreamId,
        SenderNodeId = @event.Data.SenderNodeId,
        SenderIgnored = true,
        IgnoredReason = @event.Data.Reason,
        IgnoredForVerificationFailure = @event.Data.VerificationFailed,
        IgnoredAt = @event.Data.At,
    };

    public void Apply(IEvent<InboxCursorAdvanced> @event, MessageInboxDetails view)
    {
        view.HighestSeqReceived = @event.Data.Seq;

        // A sweep that reached this point read the sender's outbox successfully, so a prior
        // ignored mark no longer holds — the sender's node file showed up, or the signature check
        // started passing, since the sweep that raised it.
        view.SenderIgnored = false;
        view.IgnoredReason = null;
        view.IgnoredForVerificationFailure = false;
        view.IgnoredAt = null;
    }

    public void Apply(IEvent<InboxSenderIgnored> @event, MessageInboxDetails view)
    {
        view.SenderIgnored = true;
        view.IgnoredReason = @event.Data.Reason;
        view.IgnoredForVerificationFailure = @event.Data.VerificationFailed;
        view.IgnoredAt = @event.Data.At;
    }

    public void Apply(IEvent<InboxSenderVouched> @event, MessageInboxDetails view)
    {
        view.SenderIgnored = false;
        view.IgnoredReason = null;
        view.IgnoredForVerificationFailure = false;
        view.IgnoredAt = null;
    }
}
