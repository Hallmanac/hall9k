namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Everything one partial stream's repair will do, decided before anything is written
/// (<see cref="PartialReplicatedStreamRepairPlanner"/>) so the whole of it can land in a single
/// <c>SaveChanges</c> and be inspected by a test without a database in the way.
/// </summary>
/// <param name="StreamId">The local stream being freed.</param>
/// <param name="Aggregate">Which aggregate's projected documents go with it.</param>
/// <param name="Held">
/// Every replicated event on the stream, rebuilt into the shape
/// <c>Hall9k.Connectors.Replication.EventReplicationInbox</c> holds a genesis-less tail in, from the
/// headers already stamped on the applied copy and the local project id its own dedupe row
/// remembers. Nothing is lost by the repair: each of these replays, in origin order, the moment the
/// genesis finally lands. Their ids are the dedupe rows to delete too: a held record's id IS the
/// origin event id that dedupe is keyed on.
/// </param>
/// <param name="DroppedNativeEventTypes">
/// The names of this node's own native events on the stream, which are dropped rather than held
/// (<see cref="PartialReplicatedStreamRules.IsDroppableNativeEvent"/>'s own doc says why). Empty for
/// the ordinary partial stream nothing local ever touched.
/// </param>
/// <param name="ClaimedRunId">
/// The run a dropped <c>TaskClaimed</c> named, so the log line can say which run the abandoned
/// claim was holding a slot for. Null when nothing among the dropped events was a claim: an
/// unobserved fact left explicitly unknown rather than filled in.
/// </param>
public sealed record PartialReplicatedStreamRepairPlan(
    Guid StreamId,
    ReplicatedAggregate Aggregate,
    IReadOnlyList<HeldReplicatedEventRecord> Held,
    IReadOnlyList<string> DroppedNativeEventTypes,
    Guid? ClaimedRunId)
{
    /// <summary>
    /// Whether this node's own <c>TaskLease</c> for the stream goes with the dropped claim. It has
    /// to: <c>Hall9k.Daemon.Dispatch.DispatchEngine.MeasureLoadAsync</c> counts a lease whose run
    /// cannot be found as a live slot, and <c>LeaseHeartbeatService</c> keeps refreshing it, so a
    /// lease left behind by a dropped claim holds one of this node's concurrency slots forever
    /// while the lease sweep never expires it. Only a task stream has one, and only a dropped claim
    /// could have left one.
    /// </summary>
    public bool DeletesTaskLease =>
        Aggregate == ReplicatedAggregate.Task && DroppedNativeEventTypes.Count > 0;
}
