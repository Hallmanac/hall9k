namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// How far one project's orchestrator feed has been drained on this node (idea 89471598, piece
/// 2) — the whole of what the feed persists. There is no second store: an item is an event past
/// <see cref="LastDrainedGlobalSequence"/> on this node's own event log that
/// <see cref="OrchestratorFeedInterest"/> admits, so draining is a cursor move and nothing is
/// ever copied anywhere.
/// <para>
/// Kept as a plain document rather than its own event-sourced stream for the same reason
/// <see cref="Replication.EventReplicationInboxCursor"/> and
/// <see cref="Replication.EventReplicationOutboxPosition"/> are: purely local, derived,
/// mechanical bookkeeping. <see cref="Id"/> is the project's own id, so there is exactly one of
/// these per project on this node and a reader needs no lookup to find it.
/// </para>
/// </summary>
public sealed class OrchestratorFeedCursor
{
    /// <summary>The project this cursor belongs to.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The highest global sequence this project's feed has been drained past — every event at or
    /// below it has been shown to an orchestrator once. Advances past every settled event
    /// actually inspected, admitted or not, much as
    /// <see cref="Replication.EventReplicationInboxCursor.HighestSeqInspected"/> does: an event
    /// the filter rejected is settled, not pending, and re-classifying it on every sweep would
    /// make the feed's cost grow with the log rather than with what is new. "Settled" is the one
    /// difference, and it is <see cref="OrchestratorFeedSelection.SettlingWindow"/>'s: a drain
    /// stops short of events young enough that a lower sequence could still be uncommitted
    /// behind them, since a cursor that passed one would never show it.
    /// </summary>
    public long LastDrainedGlobalSequence { get; set; }

    /// <summary>When the last drain moved this cursor. Null until one ever has — an unobserved
    /// time, never a stamped zero.</summary>
    public DateTimeOffset? LastDrainedAt { get; set; }

    /// <summary>Where a project's feed stands when no cursor has ever been written for it: the
    /// start of this node's own log, so a first drain hands over everything rather than starting
    /// an orchestrator off mid-story.</summary>
    public const long NeverDrained = 0;

    /// <summary>
    /// The cursor to store for a drain through <paramref name="throughSequence"/>, or null when
    /// there is nothing to write. Null covers the one case that matters: a drain that would move
    /// the cursor backwards, which happens whenever a second window drained further while this
    /// read was composing its own output. Rewinding there would re-show items somebody has
    /// already read and acted on, so the further cursor wins and this drain is a no-op.
    /// <para>
    /// The comparison is against the cursor as it was loaded, not a lock: two drains overlapping
    /// inside the gap between that load and the write are last-writer-wins, and the further one
    /// loses if it writes first. Left that way deliberately rather than defended with optimistic
    /// concurrency and a retry, because the whole cost of losing that race is that the items
    /// between the two cursors are shown again, which is what a plain read of this feed does by
    /// design anyway. Nothing is lost, and a drain never fails in front of an operator over it.
    /// </para>
    /// </summary>
    public static OrchestratorFeedCursor? Advanced(
        OrchestratorFeedCursor? existing, Guid projectId, long throughSequence, DateTimeOffset now) =>
        existing is not null && existing.LastDrainedGlobalSequence >= throughSequence
            ? null
            : new OrchestratorFeedCursor
            {
                Id = projectId,
                LastDrainedGlobalSequence = throughSequence,
                LastDrainedAt = now,
            };
}
