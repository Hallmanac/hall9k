namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed the run's pull request closed without a merge — a human
/// rejected the work on GitHub. The run fails honestly; the branch is kept (it still
/// holds unmerged work), only the worktree is removed. ClosedAt is GitHub's timestamp as
/// reported by gh; null when unreported (never guessed).
/// </summary>
public sealed record PullRequestClosed(
    Guid Id,
    DateTimeOffset? ClosedAt,
    DateTimeOffset ObservedAt);
