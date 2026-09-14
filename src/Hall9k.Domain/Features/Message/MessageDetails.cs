using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Message;

public sealed class MessageDetails
{
    public Guid Id { get; set; }
    public Guid FromNodeId { get; set; }
    public long Seq { get; set; }

    /// <summary>The envelope's own timestamp — see <see cref="MessageAggregate.QueuedAt"/>'s own doc.</summary>
    public DateTimeOffset QueuedAt { get; set; }

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
/// Reads <see cref="MessageAggregate"/>'s own stream. A sender's own database now always starts a
/// message's stream with <see cref="MessageQueued"/> (M1b's queue-then-flush split); a receiver's
/// own separate database — the identical stream id, computed the same way from the same (sender,
/// seq) — starts it with <see cref="MessageReceived"/> instead. The two are not always two
/// different databases, though: a "project"-addressed envelope matches its own sender too, so a
/// node reading its own outbox back (M1b's sweep may or may not special-case skipping it) appends
/// <see cref="MessageReceived"/> onto a stream its own <see cref="MessageQueued"/> already started,
/// in its own single database — <c>Apply(IEvent&lt;MessageReceived&gt;)</c> below exists for
/// exactly that case, not only for the aggregate's own defensive completeness. <see
/// cref="MessageSent"/> and <see cref="MessageSendFailed"/> keep their own <c>Create</c> overloads
/// too, purely defensively: neither is ever genuinely first once every sender queues before it
/// flushes, but a stream that somehow skipped its own <see cref="MessageQueued"/> should still read
/// as something rather than fail the projection outright.
/// </summary>
public sealed class MessageDetailsProjection : SingleStreamProjection<MessageDetails, Guid>
{
    public MessageDetails Create(IEvent<MessageQueued> @event) => new()
    {
        Id = @event.StreamId,
        FromNodeId = @event.Data.FromNodeId,
        Seq = @event.Data.Seq,
        QueuedAt = @event.Data.At,
        FromOwnerFingerprint = @event.Data.FromOwner,
        To = @event.Data.To,
        About = @event.Data.About,
        Kind = @event.Data.Kind,
        Body = @event.Data.Body,
    };

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
        FromOwnerFingerprint = @event.Data.FromOwner,
        To = @event.Data.To,
        About = @event.Data.About,
        Kind = @event.Data.Kind,
        Body = @event.Data.Body,
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

    public void Apply(IEvent<MessageSent> @event, MessageDetails view)
    {
        view.SentAt = @event.Data.At;
        view.SendFailed = false;
        view.SendFailureReason = null;
    }

    public void Apply(IEvent<MessageSendFailed> @event, MessageDetails view)
    {
        view.SendFailed = true;
        view.SendFailureReason = @event.Data.Reason;
        view.FromOwnerFingerprint = @event.Data.FromOwner;
        view.To = @event.Data.To;
        view.About = @event.Data.About;
        view.Kind = @event.Data.Kind;
        view.Body = @event.Data.Body;
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
