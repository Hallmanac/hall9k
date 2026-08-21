namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A human resolved a review-parked run with their own verdict (h9k review resolve —
/// the review-side sibling of h9k pr resolve, the lever decision #24 deferred).
/// MergeReady sends the run back through the loop to PullRequestOpener; NeedsFixes
/// dispatches a fix session with Reason as its findings and, like a manual pr resolve,
/// restores the automatic fix budget — the human asking is a fresh grant (log #22).
/// The run returns to UnderReview; the daemon's resume sweep picks it up.
/// <para>
/// One resolution is not a shortcut to the pull request: a merge-ready verdict on a park
/// the run reached from Verifying — the thread-dispute park (log #62), raised before the
/// gates — re-enters at Reverify instead, so gates and a review cycle run over commits no
/// gate and no reviewer has seen. The human decided the disputed thread there, not the diff.
/// </para>
/// </summary>
public sealed record ReviewParkResolved(
    Guid Id,
    ReviewVerdict Verdict,
    string? Reason,
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId);
