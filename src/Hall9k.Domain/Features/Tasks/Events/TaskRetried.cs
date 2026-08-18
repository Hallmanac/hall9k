namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A failed task returns to the queue by explicit human decision (Decisions Log #25):
/// failure of the machinery around the work must not permanently condemn the work.
/// Failed-only and human-only — Done reopens (TaskReopened), Abandoned stays a dead
/// end, and no monitor ever appends this event (never-loop-on-judgment, log #11).
/// The retry appends; it never rewrites the failure — the stream reads
/// added → … → failed → retried → claimed. Reason is mandatory: the human states why
/// another attempt should go differently. Branch is the failed run's branch as recorded
/// at dispatch (null when the failure preceded any worktree); the launcher resumes it
/// when it survives and starts clean from the base branch when it is gone.
/// </summary>
public sealed record TaskRetried(
    Guid Id,
    string Reason,
    string? Branch,
    DateTimeOffset RetriedAt,
    Guid RetriedByOwnerId);
