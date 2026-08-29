namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout could not tell the card about its merge because another Jira write was already
/// outstanding on this task (Brian's design, 2026-08-28: hall9k allows exactly one write in
/// flight per task, so a second request is refused rather than raced against the first).
/// Recorded so the notice is not silently lost the way a bare refused append would be — the
/// daemon's retry sweep attempts it, once, the same one-shot best-effort way closeout's own merge
/// comment always has been, as soon as the write that was blocking it clears.
/// </summary>
public sealed record JiraMergeNoticeQueued(Guid TaskId, DateTimeOffset QueuedAt);
