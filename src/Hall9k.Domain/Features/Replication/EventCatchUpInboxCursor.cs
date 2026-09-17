namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own durable progress reading one sender's <c>events-request</c>/<c>events-unavailable</c>
/// envelopes for one local project (idea 202383dc, M2b, task 9408d525) — the catch-up-protocol
/// analogue of <see cref="EventReplicationInboxCursor"/>, kept as its own plain document (purely
/// local, derived, mechanical bookkeeping) and its own cursor stream so this second, independent
/// read of the identical outbox ref never disturbs the events-replication cursor's own position.
/// <see cref="Id"/> is <see cref="EventReplicationStreamId.ForCatchUpInboxCursor"/>.
/// </summary>
public sealed class EventCatchUpInboxCursor
{
    public Guid Id { get; set; }

    public Guid SenderNodeId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The highest seq in the sender's own outbox this node has inspected for catch-up
    /// protocol envelopes — advances past every envelope actually looked at, whether or not it
    /// carried an events-request or events-unavailable payload.</summary>
    public long HighestSeqInspected { get; set; }
}
