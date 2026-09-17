namespace Hall9k.Domain.Features.Node;

/// <summary>
/// The first time event replication ever runs on this node, it records the node's own current
/// global event sequence as its switch-on point (idea 202383dc, M2a; Brian's 2026-09-13 ruling on
/// migrating his two existing nodes: "no wipe and no re-adoption; each node keeps its history;
/// replication records a switch-on point per node and nothing before it travels"). Every event at
/// or below <see cref="SwitchOnGlobalSequence"/> is this node's own pre-replication history and is
/// handled by this node exactly as it was before replication existed — it never rides an outbox,
/// and it is never backfilled from elsewhere. Appended exactly once per node
/// (<see cref="NodeDecider.SwitchOnReplication"/> is called only when
/// <see cref="NodeAggregate.ReplicationSwitchOnSequence"/> is still null); node-scoped, since it is
/// a fact about this install alone.
/// </summary>
public sealed record ReplicationSwitchedOn(Guid Id, long SwitchOnGlobalSequence, DateTimeOffset At);
