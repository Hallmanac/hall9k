namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// A replicated tail event <c>Hall9k.Connectors.Replication.EventReplicationInbox</c> held rather
/// than applied, because the stream it targets has never started here and this record is not that
/// aggregate's own genesis (<see cref="AggregateGenesisEventTypes"/>) — the shape a task whose
/// <c>TaskAdded</c> predates the sender's outbox, or a run whose parent task never arrived, both
/// take. Kept, rather than discarded the way a permanently-skipped poison record is
/// (<see cref="ReplicatedEventRecord.Applied"/> false, a terminal state nothing ever revisits),
/// because this one IS recoverable: the moment a later envelope actually carries the missing
/// genesis, that same inbox replays every held record for this stream, oldest first, and each
/// document is deleted the moment its own replay lands — so the story completes in order and
/// nothing held is ever lost.
/// <para>
/// Keyed by <see cref="Id"/> = the origin event id, the same identity <see cref="ReplicatedEventRecord"/>
/// dedupes on, so a re-delivered copy of an already-held record only ever overwrites its own row
/// rather than piling up a duplicate.
/// </para>
/// <para>
/// Carries the wire-format record verbatim (<see cref="RecordJson"/>), plus the sender and project
/// context it arrived under, so replay needs no second round trip to the sender: this is the whole
/// of what that inbox's own apply method would have needed to apply it the moment it first
/// arrived, preserved for the moment it actually can.
/// </para>
/// </summary>
public sealed class HeldReplicatedEventRecord
{
    public Guid Id { get; set; }

    public Guid StreamId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid SenderNodeId { get; set; }

    public string? OriginProjectKey { get; set; }

    /// <summary>The wire-format record itself (<see cref="EventReplicationCodec.ReplicatedEventRecord"/>), JSON-encoded.</summary>
    public string RecordJson { get; set; } = string.Empty;

    /// <summary>
    /// The ordering key a stream's held tail is replayed in once its genesis arrives: the origin's
    /// own sequence, safe to order by directly for records sharing one origin node (the ordinary
    /// case a held tail is), with <see cref="OriginNodeId"/> and this document's own <see cref="HeldAt"/>
    /// as a deterministic tie-break the rare cross-origin tail needs.
    /// </summary>
    public long OriginSequence { get; set; }

    public Guid OriginNodeId { get; set; }

    public DateTimeOffset HeldAt { get; set; }
}
