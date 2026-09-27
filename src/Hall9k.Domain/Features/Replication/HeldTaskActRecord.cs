namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// A replicated Task or Run "act" (idea 6be68ee2, trust-ledger finding 5) a non-owner sender's
/// verdict could not yet be settled for — the task it targets already exists here
/// (<see cref="HeldReplicatedEventRecord"/> is the sibling table for a genuinely missing genesis),
/// but the specific fact this act's own conditional check needs — its current assignment or
/// holder, or, for a Run act, its task at all — has not replicated here yet, which is
/// indistinguishable, from this node's own state alone, from the act genuinely never having been
/// authorized. Held rather than dropped so the still-missing fact gets a chance to arrive
/// (<c>Hall9k.Connectors.Replication.EventCatchUpCoordinator.RequestStreamBroadcastAsync</c>'s own
/// ask, targeting <see cref="TaskId"/>).
/// <para>
/// Keyed by <see cref="Id"/> = the origin event id, the same identity
/// <see cref="ReplicatedEventRecord"/> and <see cref="HeldReplicatedEventRecord"/> both dedupe on.
/// Every LATER record from the identical <see cref="OriginNodeId"/> targeting the identical
/// <see cref="StreamId"/> is held here too, in <see cref="OriginSequence"/> order, the moment one
/// record from that origin is already held — never applied ahead of it, which would let a
/// subsequent record's own append set this stream's per-origin high-water mark past the held
/// one's own sequence and then refuse it as out of order the moment it finally clears
/// (<c>EventReplicationInbox.OriginHighWaterAsync</c>'s own guard).
/// </para>
/// <para>
/// Re-checked after every replication read that applies anything for this project, whichever
/// sender's read it was: the fact a hold is waiting on may arrive from a completely different
/// sender than the one whose act is held. <see cref="HeldAt"/> is set once, at first hold, and
/// never refreshed by a later read that leaves this record still unresolved — the moment it turns
/// more than 24 hours old, the whole queue behind it is dropped with one log line, never silently.
/// </para>
/// </summary>
public sealed class HeldTaskActRecord
{
    public Guid Id { get; set; }

    /// <summary>This act's own effective stream — the task's own stream for a Task-namespaced act,
    /// or the run's own stream for a Run-namespaced one.</summary>
    public Guid StreamId { get; set; }

    /// <summary>The task this act's conditional verdict is judged against — <see cref="StreamId"/>
    /// itself for a Task act, or the run's own <c>TaskId</c> for a Run act.</summary>
    public Guid TaskId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid SenderNodeId { get; set; }

    /// <summary>The verified key fingerprint this record's own sender read carried — stored here
    /// (unlike <see cref="HeldReplicatedEventRecord.SenderFingerprint"/>'s own doc, which is never
    /// actually consulted on replay) because the conditional verdict this record is held FOR runs
    /// again at replay, against this exact value.</summary>
    public string? SenderFingerprint { get; set; }

    public string? OriginProjectKey { get; set; }

    /// <summary>The wire-format record itself (<see cref="EventReplicationCodec.ReplicatedEventRecord"/>), JSON-encoded.</summary>
    public string RecordJson { get; set; } = string.Empty;

    public long OriginSequence { get; set; }

    public Guid OriginNodeId { get; set; }

    /// <summary>When this record was FIRST held — never refreshed by a later re-check that leaves
    /// it still unresolved, so the 24-hour expiry measures from the fact's own first missed
    /// arrival, not from however many sweeps have since looked at it.</summary>
    public DateTimeOffset HeldAt { get; set; }
}
