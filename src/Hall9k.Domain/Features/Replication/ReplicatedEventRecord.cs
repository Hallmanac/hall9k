namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own record that one origin event has already been RESOLVED as a replicated fact —
/// the dedupe index a re-delivered batch checks against, keyed by the origin's own Marten event id
/// (<see cref="Id"/>) rather than a local one, since local version numbers are local to each node's
/// own store and identity travels as the origin event id alone. A plain document for the same
/// reason <see cref="EventReplicationOutboxPosition"/> is: purely local bookkeeping, never a domain
/// fact of its own.
/// <para>
/// Resolved does not always mean applied: <see cref="Applied"/> is true for the ordinary case
/// (idea 202383dc, M2a: "skipped when the origin event id is already stored") and false for an
/// origin event this node decided, permanently, never to apply — a poison record whose own inline
/// projection threw, or a follow-up refused because its stream's true genesis already failed this
/// way (<c>EventReplicationInbox.ApplyAsync</c>'s own doc). Both cases still need this row: a
/// permanently-skipped origin event must never be retried either, and it is never coming back. A
/// reader asking "did this project actually receive real history" — <c>MessageSweepEngine
/// .HasAnyLocalHistoryAsync</c>'s own bootstrap gate — filters on <see cref="Applied"/> rather than
/// on this row's mere existence (independent pre-PR review, cycle 1, adversarial lens, low).
/// </para>
/// </summary>
public sealed class ReplicatedEventRecord
{
    /// <summary>The origin event's own Marten event id.</summary>
    public Guid Id { get; set; }

    public Guid StreamId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>False for an origin event this node permanently skipped rather than applied — see
    /// this type's own doc. Defaults true so every caller that means the ordinary case (the whole
    /// of this type before this field existed) needs no change.</summary>
    public bool Applied { get; set; } = true;
}
