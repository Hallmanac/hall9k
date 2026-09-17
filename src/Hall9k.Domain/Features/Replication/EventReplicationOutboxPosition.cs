namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own durable progress replicating one project's own local events outward (idea
/// 202383dc, M2a: "the flush position is durable in the node's store, so a restart never re-sends
/// or skips"). A plain document, not an event-sourced aggregate — it is derived, mechanical
/// bookkeeping about how far a sweep has scanned, never a domain fact worth a stream of its own,
/// the same reason <c>MessageSweepEngine</c>'s own in-memory <c>_lastKnownTips</c> and
/// <c>MessageOutbox</c>'s own <c>_lastSquashedSurvivorSeqs</c> are plain state rather than events —
/// the difference here is only that a restart must not lose it, so it is stored rather than kept
/// in memory. <see cref="Id"/> is the project's own local id, one document per project.
/// </summary>
public sealed class EventReplicationOutboxPosition
{
    public Guid Id { get; set; }

    /// <summary>The highest global event sequence this node has already scanned (and, where
    /// eligible, flushed) for this project — never an event past this point is scanned again.</summary>
    public long LastFlushedGlobalSequence { get; set; }
}
