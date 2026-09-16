namespace Hall9k.Domain.Features.Message;

/// <summary>One sender's own inbox stream in this node's own store, keyed by
/// <see cref="MessageStreamId.ForInbox"/> — this node's own cursor and vouch status for that
/// sender's outbox. Never in the repository (idea 202383dc: "cursors are local").</summary>
public sealed class MessageInboxAggregate
{
    /// <summary>The stream id itself (<see cref="MessageStreamId.ForInbox"/>) — Marten's own live
    /// aggregation needs a settable <c>Id</c> to compile this type's schema, even though nothing
    /// here is ever stored as a document.</summary>
    public Guid Id { get; private set; }

    public Guid SenderNodeId { get; private set; }

    /// <summary>This node's own local project id this cursor belongs to (idea 202383dc, M2).</summary>
    public Guid ProjectId { get; private set; }

    public long HighestSeqReceived { get; private set; }
    public bool SenderIgnored { get; private set; }
    public string? IgnoredReason { get; private set; }
    public bool IgnoredForVerificationFailure { get; private set; }
    public DateTimeOffset? IgnoredAt { get; private set; }

    public void Apply(InboxCursorAdvanced @event)
    {
        Id = MessageStreamId.ForInbox(@event.SenderNodeId, @event.ProjectId);
        SenderNodeId = @event.SenderNodeId;
        ProjectId = @event.ProjectId;
        HighestSeqReceived = @event.Seq;
        SenderIgnored = false;
        IgnoredReason = null;
        IgnoredForVerificationFailure = false;
        IgnoredAt = null;
    }

    public void Apply(InboxSenderIgnored @event)
    {
        Id = MessageStreamId.ForInbox(@event.SenderNodeId, @event.ProjectId);
        SenderNodeId = @event.SenderNodeId;
        ProjectId = @event.ProjectId;
        SenderIgnored = true;
        IgnoredReason = @event.Reason;
        IgnoredForVerificationFailure = @event.VerificationFailed;
        IgnoredAt = @event.At;
    }

    public void Apply(InboxSenderVouched @event)
    {
        Id = MessageStreamId.ForInbox(@event.SenderNodeId, @event.ProjectId);
        SenderNodeId = @event.SenderNodeId;
        ProjectId = @event.ProjectId;
        SenderIgnored = false;
        IgnoredReason = null;
        IgnoredForVerificationFailure = false;
        IgnoredAt = null;
    }
}
