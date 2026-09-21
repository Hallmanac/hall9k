namespace Hall9k.Connectors.Replication;

/// <summary>
/// What one sweep's held-tail pass actually did
/// (<see cref="EventCatchUpCoordinator.RequestHeldTailStreamsAsync"/>).
/// </summary>
/// <param name="AsksMinted">Broadcast stream requests this sweep put on the wire.</param>
/// <param name="StreamsGivenUp">
/// Streams marked <see cref="Domain.Features.Replication.HeldReplicatedEventRecord.CatchUpGivenUp"/>
/// this sweep: three asks made, the last one long since closed, and the genesis still missing.
/// </param>
/// <param name="StreamsToReplay">
/// Streams whose held records are waiting on nothing but a replay: the local stream exists here,
/// so the genesis is not missing at all and the records are the residue of a read that started the
/// stream and then failed before draining them. Handed back rather than acted on here, because the
/// replay belongs to <c>EventReplicationInbox</c>, which owns every rule about applying a
/// replicated event; the caller (the daemon's own sweep) passes each one to that inbox's
/// <c>ReplayHeldTailAsync</c>. Nothing else ever clears them, and while they sit there they spend
/// a slot of this sweep's own cap and count as tail-only in <c>h9k status</c>.
/// </param>
public sealed record HeldTailSweepResult(int AsksMinted, int StreamsGivenUp, IReadOnlyList<Guid> StreamsToReplay)
{
    /// <summary>A sweep with no held records at all — the steady state.</summary>
    public static HeldTailSweepResult Nothing { get; } = new(0, 0, []);
}
