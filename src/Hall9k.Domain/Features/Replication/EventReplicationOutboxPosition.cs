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
    /// lenses). This field alone cannot tell "already sent" apart from "skipped as private while a
    /// later stream's own event pushed this mark past it" — <see cref="PendingPrivateSequences"/> is
    /// what makes that distinction (independent pre-PR review, cycle 5, adversarial lens).
    /// </summary>
    public long HighestQueuedSequence { get; set; }

    /// <summary>
    /// The exact global sequence of every event this project has found still private and skipped,
    /// that has not yet actually been queued into an envelope — never just the earliest one, and
    /// never cleared by a later sweep's own progress elsewhere in the project.
    /// <para>
    /// <see cref="HighestQueuedSequence"/> alone cannot tell a held-back event apart from an
    /// already-sent one once some other, later-sequence stream in the same project gets queued
    /// while the held-back event is still skipped: both now sit below the mark. Without this set, a
    /// scan resuming after the flag clears reads the held-back event's own sequence as "already
    /// queued" from the mark alone and drops it forever — the idea or task that was private never
    /// fully reaches a teammate, and the receiving inbox can start a phantom stream from whatever
    /// event happens to arrive first (independent pre-PR review, cycle 5, adversarial lens: a
    /// scenario built from exactly this ordering, on <c>EventReplicationOutbox.cs:155</c>).
    /// </para>
    /// <para>
    /// A scan checks this set before trusting <see cref="HighestQueuedSequence"/>'s "already sent"
    /// reading for any one candidate: a sequence in this set is queued now regardless of the mark,
    /// then removed. A sequence still found private this sweep is (re)added. The set only ever holds
    /// events genuinely still owed to a teammate, so it stays small in the ordinary case — bounded
    /// by however many events a single private draft's own stream carries, not by how much other
    /// project activity happens while it stays private.
    /// </para>
    /// </summary>
    public List<long> PendingPrivateSequences { get; set; } = [];
}
