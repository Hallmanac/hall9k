namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own record that one origin event has already been applied as a replicated fact
/// (idea 202383dc, M2a: "skipped when the origin event id is already stored") — the dedupe index a
/// re-delivered batch checks against, keyed by the origin's own Marten event id
/// (<see cref="Id"/>) rather than a local one, since local version numbers are local to each node's
/// own store and identity travels as the origin event id alone. A plain document for the same
/// reason <see cref="EventReplicationOutboxPosition"/> is: purely local bookkeeping, never a domain
/// fact of its own.
/// </summary>
public sealed class ReplicatedEventRecord
{
    /// <summary>The origin event's own Marten event id.</summary>
    public Guid Id { get; set; }

    public Guid StreamId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
