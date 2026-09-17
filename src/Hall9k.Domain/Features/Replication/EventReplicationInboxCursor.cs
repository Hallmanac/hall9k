namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own durable progress applying one sender's <c>events</c>-kind envelopes for one
/// local project (idea 202383dc, M2a) — the events-replication analogue of
/// <c>Hall9k.Domain.Features.Message.MessageInboxAggregate</c>'s own cursor, kept as a plain
/// document rather than its own event-sourced stream for the same reason
/// <see cref="EventReplicationOutboxPosition"/> is: purely local, derived, mechanical bookkeeping.
/// <see cref="Id"/> is <see cref="EventReplicationStreamId.ForInboxCursor"/>.
/// </summary>
public sealed class EventReplicationInboxCursor
{
    public Guid Id { get; set; }

    public Guid SenderNodeId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The highest seq in the sender's own outbox this node has inspected — advances past
    /// every envelope actually looked at, whether or not it carried an events-kind payload.</summary>
    public long HighestSeqInspected { get; set; }

    /// <summary>True when this sender is currently ignored — either because no node file vouches
    /// for its outbox at all, or because <see cref="IgnoredForProjectKeyMismatch"/> is true and a
    /// specific envelope carried a project key that does not match this project's own. Idea
    /// 202383dc: "an events envelope from an unverified sender is ignored and named in status."</summary>
    public bool SenderIgnored { get; set; }

    public string? IgnoredReason { get; set; }

    /// <summary>Distinguishes a mismatch mark from a not-vouched mark the same way
    /// <c>MessageInboxDetails.IgnoredForVerificationFailure</c> does, so the two clear on different
    /// rules: a not-vouched mark clears the moment the sender is vouched again, even on a sweep that
    /// inspects nothing new, while a mismatch mark is a fact about a specific envelope and stays set
    /// until a sweep that genuinely inspects fresh content finds no mismatch in it.</summary>
    public bool IgnoredForProjectKeyMismatch { get; set; }

    public DateTimeOffset? IgnoredAt { get; set; }
}
