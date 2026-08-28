namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The owner's word (h9k review resolve --merge-ready, on a pr-review task) that the
/// findings report has been walked and directed — dismissed, commented on by hand, or
/// posted on the owner's behalf — and the task may close. Distinct from ReviewParkResolved:
/// there is no diff of this run's own to proceed to a pull request with, so resolving a
/// pr-review park moves the run to UnderReview only to be picked back up by PrReviewEngine,
/// which finalizes it directly (removes the worktree, completes the task) rather than
/// re-entering any review loop.
/// </summary>
public sealed record PrReviewDelivered(
    Guid Id,
    string? Reason,
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId);
