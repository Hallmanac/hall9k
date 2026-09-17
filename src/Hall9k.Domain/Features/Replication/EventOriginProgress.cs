namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own highest applied global sequence from one origin node, for one local project
/// (idea 202383dc, M2b, task 9408d525) — a plain document, not an event-sourced aggregate, the same
/// reason <see cref="EventReplicationOutboxPosition"/> is: purely local, derived bookkeeping, never
/// a domain fact of its own. The coarse "since" bound an outbound gap-fill events-request is built
/// from: the origin's own global sequence is not contiguous across projects and node/owner-scoped
/// events, so "greater than this" is always a safe superset of what is genuinely missing from that
/// origin, never a subset — any overlap a peer answers with is harmless (dedupe by origin event id).
/// <see cref="Id"/> is <see cref="EventReplicationStreamId.ForOriginProgress"/>.
/// </summary>
public sealed class EventOriginProgress
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid OriginNodeId { get; set; }

    public long HighestOriginSequenceApplied { get; set; }
}

