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

    /// <summary>
    /// The first global sequence number a sweep found missing between two events it could read (a
    /// writer took the number but had not committed it yet), while that wait is still running; zero
    /// when no gap is being waited on. The outbox queues nothing past this number until it fills or
    /// the bounded wait (<c>EventReplicationOutbox.MaxSequenceGapWait</c>) measured from
    /// <see cref="WaitingSince"/> has passed.
    /// </summary>
    public long WaitingForSequence { get; set; }

    /// <summary>The sweep time (the <c>now</c> the outbox was handed, never a wall clock) at which
    /// <see cref="WaitingForSequence"/> was first seen missing, so a restart neither skips the gap nor
    /// restarts its wait. Meaningful only while <see cref="WaitingForSequence"/> is not zero.</summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>The highest committed global sequence the sweep that started the running wait could
    /// see, so every missing number at or below it was already missing when the clock started and
    /// is covered by the same wait; a missing number above it starts a wait of its own. Meaningful
    /// only while <see cref="WaitingForSequence"/> is not zero.</summary>
    public long WaitingThroughSequence { get; set; }

    /// <summary>True once a sweep has seen the running wait's deadline pass with the gap still open.
    /// The gap is only given up on the sweep after that, so time the machine spent asleep (the whole
    /// wait elapsing between two sweeps) never expires a wait before an in-flight writer has had a
    /// sweep to finish. Meaningful only while <see cref="WaitingForSequence"/> is not zero.</summary>
    public bool WaitDeadlinePassed { get; set; }

    /// <summary>
    /// Every missing sequence number at or below this one has already been waited out and is never
    /// waited on again. A held-back private event keeps <see cref="LastFlushedGlobalSequence"/> low, so
    /// sweeps keep rescanning the region above it, and without this mark that rescan would find the
    /// same dead number again and start a fresh wait on every sweep.
    /// </summary>
    public long GapsWaitedOutThroughSequence { get; set; }

    /// <summary>
    /// Every stream id a scope-widening event (<c>IdeaScopeSet</c>, <c>IdeaPrivacySet</c>,
    /// <c>TaskScopeSet</c>, <c>TaskPrivacySet</c>, or <c>TaskPublished</c>) has marked for a one-time
    /// full-history resend at its own new scope (idea 8c5993c5) — never cleared until that resend
    /// actually runs, so a scope change followed by a crash finds the stream again rather than
    /// silently leaving a teammate with only a partial history. Bounded by however many streams have
    /// widened scope since the last time this project's outbox actually ran, which is ordinary
    /// project activity, not a growth risk the way <see cref="PendingPrivateSequences"/> guards
    /// against for a single long-held-private stream.
    /// </summary>
    public List<Guid> PendingFullResendStreamIds { get; set; } = [];
}
