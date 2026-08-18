namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A failed task returns to the queue by explicit human decision (Decisions Log #25):
/// infrastructure failure around finished work must not strand that work in a terminal
/// state. The retry appends — it never rewrites: the stream reads added → … → failed →
/// retried → claimed, and the failure stays visible. Branch is the failed run's branch
/// as observed at retry time (null when the failure predates any run record); the
/// launcher resumes it when it still exists and starts clean from the base branch when
/// it is gone. Distinct from TaskReopened (Done-only, PR closeout) and human-only by
/// design — no monitor appends this (never loop on judgment, log #11) — so there is no
/// Automatic flag and no budget interaction.
/// </summary>
public sealed record TaskRetried(
    Guid Id,
    Guid? PreviousRunId,
    string? Branch,
    string Reason,
    DateTimeOffset RetriedAt,
    Guid RetriedByOwnerId);
