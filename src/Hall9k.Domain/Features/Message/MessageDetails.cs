using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Message;

public sealed class MessageDetails
{
    public Guid Id { get; set; }
    public Guid FromNodeId { get; set; }
    public long Seq { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public bool SendFailed { get; set; }
    public string? SendFailureReason { get; set; }
    public int ResendCount { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }
    public string? FromOwnerFingerprint { get; set; }
    public string? To { get; set; }
    public string? About { get; set; }
    public string? Kind { get; set; }
    public string? Body { get; set; }

    public DateTimeOffset? HandledAt { get; set; }
}

/// <summary>
/// Reads <see cref="MessageAggregate"/>'s own stream. Three <c>Create</c> overloads, one per event
/// type that can genuinely be the first ever appended to a message's stream: usually a sender's own
/// database starts it with <see cref="MessageSent"/> or <see cref="MessageSendFailed"/>, and a
/// receiver's own separate database — the identical stream id, computed the same way from the same
/// (sender, seq) — starts it with <see cref="MessageReceived"/> instead. The two are not always two
/// different databases, though: a "project"-addressed envelope matches its own sender too, so a
/// node reading its own outbox back (M1b's sweep may or may not special-case skipping it) appends
/// <see cref="MessageReceived"/> onto a stream its own <see cref="MessageSent"/> already started, in
/// its own single database — <c>Apply(IEvent&lt;MessageReceived&gt;)</c> below exists for exactly
/// that case, not only for the aggregate's own defensive completeness.
/// </summary>
public sealed class MessageDetailsProjection : SingleStreamProjection<MessageDetails, Guid>
{
    public MessageDetails Create(IEvent<MessageSent> @event) => new()
    {
        Id = @event.StreamId,
        FromNodeId = @event.Data.FromNodeId,
        Seq = @event.Data.Seq,
        SentAt = @event.Data.At,
    };

    public MessageDetails Create(IEvent<MessageSendFailed> @event) => new()
    {
        Id = @event.StreamId,
        FromNodeId = @event.Data.FromNodeId,
        Seq = @event.Data.Seq,
        SendFailed = true,
        SendFailureReason = @event.Data.Reason,
    };

    public MessageDetails Create(IEvent<MessageReceived> @event) => new()
    {
        Id = @event.StreamId,
        FromNodeId = @event.Data.FromNodeId,
        Seq = @event.Data.Seq,
        SentAt = @event.Data.SentAt,
        ReceivedAt = @event.Data.ReceivedAt,
        FromOwnerFingerprint = @event.Data.FromOwnerFingerprint,
        To = @event.Data.To,
        About = @event.Data.About,
        Kind = @event.Data.Kind,
        Body = @event.Data.Body,
    };

    public void Apply(IEvent<MessageSendFailed> @event, MessageDetails view)
    {
        view.SendFailed = true;
        view.SendFailureReason = @event.Data.Reason;
    }

    public void Apply(IEvent<MessageResent> @event, MessageDetails view)
    {
        view.SendFailed = false;
        view.SendFailureReason = null;
        view.SentAt = @event.Data.At;
        view.ResendCount++;
    }

    public void Apply(IEvent<MessageReceived> @event, MessageDetails view)
    {
        view.SentAt = @event.Data.SentAt;
        view.ReceivedAt = @event.Data.ReceivedAt;
        view.FromOwnerFingerprint = @event.Data.FromOwnerFingerprint;
        view.To = @event.Data.To;
        view.About = @event.Data.About;
        view.Kind = @event.Data.Kind;
        view.Body = @event.Data.Body;
    }

    public void Apply(IEvent<MessageHandled> @event, MessageDetails view) => view.HandledAt = @event.Data.At;
}
