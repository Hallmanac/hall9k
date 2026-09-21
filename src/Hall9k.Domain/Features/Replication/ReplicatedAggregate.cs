namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Which of the four replicated aggregates a stream belongs to, read off the event types actually
/// on it (<see cref="PartialReplicatedStreamRules.AggregateOf"/>), which is the one fact deciding which
/// projected documents a partial stream's repair deletes. Task, Idea, Epic and Run are exactly the
/// aggregates that mint a genesis event (<see cref="AggregateGenesisEventTypes"/>), which is what
/// makes "started from something other than its genesis" a meaningful question about them at all;
/// <see cref="Unknown"/> is every other stream in the store, including the Project aggregate, whose
/// per-install lifecycle events are MEANT to phantom-stream under a foreign id
/// (<see cref="ProjectStreamReplicationRules.IsProjectLifecycleEvent"/>).
/// <para>
/// An enum rather than a value object because nothing persists it: it is an in-process
/// classification a repair pass computes, reads once, and discards (AGENTS.md's own type rule).
/// </para>
/// </summary>
public enum ReplicatedAggregate
{
    /// <summary>Not one of the four replicated aggregates at all, so never a stream this repair selects.</summary>
    Unknown,

    Task,

    Idea,

    Epic,

    Run,
}
