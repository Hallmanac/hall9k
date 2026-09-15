namespace Hall9k.Domain.Features.Message;

/// <summary>One message's own stream, keyed by <see cref="MessageStreamId.ForMessage"/> (the
/// sender node plus the seq it used) — the sender's own local database applies
/// <see cref="MessageSent"/>/<see cref="MessageSendFailed"/>/<see cref="MessageResent"/>; a
/// receiver's own local database applies <see cref="MessageReceived"/>/<see cref="MessageHandled"/>
/// on the identical stream id, since it is the same message either way.</summary>
public sealed class MessageAggregate
{
    /// <summary>The stream id itself (<see cref="MessageStreamId.ForMessage"/>) — Marten's own live
    /// aggregation needs a settable <c>Id</c> to compile this type's schema, even though nothing
    /// here is ever stored as a document.</summary>
    public Guid Id { get; private set; }

    public Guid FromNodeId { get; private set; }
    public long Seq { get; private set; }

    /// <summary>
    /// The envelope's own timestamp — set once, at <see cref="Apply(MessageQueued)"/>, and never
    /// moved by a later flush attempt or retry: the wire content a flush re-encodes must stay
    /// byte-for-byte identical across retries. The sweep's own squash-by-age filter (idea
    /// 202383dc, M1b) deliberately reads <see cref="SentAt"/> instead, never this field: a push
    /// rejected for longer than the retention window still lands with a fresh <see cref="SentAt"/>
    /// the moment the next flush finally succeeds, so measuring from this much older queue time
    /// would force-remove an envelope moments after a reader's first chance to fetch it
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    public DateTimeOffset QueuedAt { get; private set; }

    /// <summary>Null until a flush lands this envelope in the outbox transport — the flush
    /// sweep's own "still needs flushing" query is exactly <c>SentAt is null</c>, which is true
    /// both for a freshly queued envelope and for one whose last flush attempt failed.</summary>
    public DateTimeOffset? SentAt { get; private set; }
    public bool SendFailed { get; private set; }
    public string? SendFailureReason { get; private set; }
    public int ResendCount { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }
    public string? FromOwnerFingerprint { get; private set; }
    public string? To { get; private set; }
    public string? About { get; private set; }
    public string? Kind { get; private set; }
    public string? Body { get; private set; }

    public DateTimeOffset? HandledAt { get; private set; }

    public void Apply(MessageQueued @event)
    {
        Id = MessageStreamId.ForMessage(@event.FromNodeId, @event.Seq);
        FromNodeId = @event.FromNodeId;
        Seq = @event.Seq;
        QueuedAt = @event.At;
        FromOwnerFingerprint = @event.FromOwner;
        To = @event.To;
        About = @event.About;
        Kind = @event.Kind;
        Body = @event.Body;
    }

    public void Apply(MessageSent @event)
    {
        Id = MessageStreamId.ForMessage(@event.FromNodeId, @event.Seq);
        FromNodeId = @event.FromNodeId;
        Seq = @event.Seq;
        SentAt = @event.At;
        SendFailed = false;
        SendFailureReason = null;
    }

    public void Apply(MessageSendFailed @event)
    {
        Id = MessageStreamId.ForMessage(@event.FromNodeId, @event.Seq);
        FromNodeId = @event.FromNodeId;
        Seq = @event.Seq;
        SendFailed = true;
        SendFailureReason = @event.Reason;
        FromOwnerFingerprint = @event.FromOwner;
        To = @event.To;
        About = @event.About;
        Kind = @event.Kind;
        Body = @event.Body;
    }

    public void Apply(MessageResent @event)
    {
        SendFailed = false;
        SendFailureReason = null;
        SentAt = @event.At;
        ResendCount++;
    }

    public void Apply(MessageReceived @event)
    {
        Id = MessageStreamId.ForMessage(@event.FromNodeId, @event.Seq);
        FromNodeId = @event.FromNodeId;
        Seq = @event.Seq;
        SentAt = @event.SentAt;
        ReceivedAt = @event.ReceivedAt;
        FromOwnerFingerprint = @event.FromOwnerFingerprint;
        To = @event.To;
        About = @event.About;
        Kind = @event.Kind;
        Body = @event.Body;
    }

    public void Apply(MessageHandled @event) => HandledAt = @event.At;
}
