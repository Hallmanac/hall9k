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
/// dedupes on, so a re-delivered copy of an already-held record can never pile up a duplicate. That
/// inbox leaves the existing row untouched rather than storing over it: the ask bookkeeping below
/// and the original <see cref="HeldAt"/> both live here, and an upsert would clear them every time
/// a peer answered a held-tail ask by serving the same tail again.
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

    /// <summary>
    /// How many broadcast stream requests this node has minted for <see cref="StreamId"/> while
    /// this record sat held — the count the daemon's own held-tail sweep stops at
    /// (<c>EventCatchUpCoordinator.MaxHeldTailAttempts</c>). Counted on the held records rather
    /// than on the request documents because the records are what the sweep already reads and what
    /// disappear the moment the genesis lands: a stream whose tail completed leaves no count
    /// behind to keep re-reading, and a stream nobody in the fleet can complete stops being asked
    /// about after three tries instead of being asked forever.
    /// </summary>
    public int CatchUpAttempts { get; set; }

    /// <summary>When the most recent of those asks was minted, or null while none has been —
    /// an observation, never a substitute for <see cref="HeldAt"/>.</summary>
    public DateTimeOffset? LastCatchUpAskedAt { get; set; }

    /// <summary>
    /// Set once <see cref="CatchUpAttempts"/> reached the stop AND that last ask has since gone
    /// unanswered past its own cooldown, which is the sweep that would have minted a fourth: no
    /// further ask is minted for this stream, and <c>h9k status</c> counts it among the streams
    /// given up on rather than among the ones still being chased. Never at the moment the last ask
    /// went out, since the fleet may still answer it and the pane would be calling a stream
    /// abandoned while its own request was in flight. Not a terminal verdict on the record itself — the held tail is
    /// still kept, and still replays the instant some later envelope happens to carry the genesis;
    /// what has stopped is only this node asking for it. The shape this exists for is a stream
    /// whose genesis event carries an event type name the answering build no longer knows, which
    /// no number of asks will ever produce.
    /// </summary>
    public bool CatchUpGivenUp { get; set; }

    /// <summary>
    /// The build this node was running when <see cref="CatchUpGivenUp"/> was set — this install's
    /// own <c>CliVersion.Current</c>/<c>DaemonVersion.Current</c> at the moment of give-up, null
    /// for a record given up before this field existed. A give-up this node marked cannot know
    /// whether the fleet's own build has since changed, so
    /// <c>Hall9k.Connectors.Replication.EventCatchUpCoordinator.GivenUpMarkStillStands</c> reads
    /// this against the build running NOW: a give-up recorded on an older build is treated
    /// as not given up at all, earning the stream three fresh asks the moment this node itself
    /// upgrades, rather than standing forever on a verdict a since-fixed peer never got the chance
    /// to answer for. A record with no recorded version counts as older unconditionally, the same
    /// as every pre-existing given-up row.
    /// </summary>
    public string? GivenUpOnBuildVersion { get; set; }
}
