namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// This node's own durable progress replicating one project's own local events outward (idea
/// 202383dc, M2a: "the flush position is durable in the node's store, so a restart never re-sends
/// or skips"). A plain document, not an event-sourced aggregate — it is derived, mechanical
/// bookkeeping about how far a sweep has scanned, never a domain fact worth a stream of its own,
/// the same reason <c>MessageSweepEngine</c>'s own in-memory <c>_lastKnownTips</c> and
/// <c>MessageOutbox</c>'s own <c>_lastSquashedSurvivorSeqs</c> are plain state rather than events —
/// the difference here is only that a restart must not lose it, so it is stored rather than kept
/// in memory. <see cref="Id"/> is the project's own local id, one document per project.
/// </summary>
public sealed class EventReplicationOutboxPosition
{
    public Guid Id { get; set; }

    /// <summary>The highest global event sequence this node has already scanned (and, where
    /// eligible, flushed) for this project — never an event past this point is scanned again. Capped
    /// at the earliest still-private event a scan finds (never past it), so a scan resumes from
    /// there — and keeps re-examining it — for as long as the flag stays set.</summary>
    public long LastFlushedGlobalSequence { get; set; }

    /// <summary>
    /// The highest global event sequence this project has ever actually queued into an envelope —
    /// unlike <see cref="LastFlushedGlobalSequence"/>, never capped back down by a later still-private
    /// event, so it only ever grows. A private task or idea elsewhere in the project holds
    /// <see cref="LastFlushedGlobalSequence"/> below events that were already sent, which would
    /// otherwise make every later sweep re-scan and re-queue those same eligible events as brand-new
    /// envelopes, for as long as the flag stayed set (independent pre-PR review, cycle 3, both
    /// lenses). This field is what a scan checks instead, so an event already sent is recognized and
    /// skipped rather than requeued, however many sweeps happen before the private flag clears.
    /// </summary>
    public long HighestQueuedSequence { get; set; }
}
