namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The reviewer's review is posted and this pr-review task is now watching the pull request for
/// the author's answer instead of closing (task: a pr-review task stays open while the pull
/// request's review threads are unresolved). Moves the task to
/// <see cref="TaskState.AwaitingAuthor"/>.
/// <para>
/// Appended by <c>PrReviewEngine.FinalizeAsync</c> in place of the <c>TaskCompleted</c> it used
/// to append there, and by exactly the same three routes that reach that finalize: <c>h9k pr
/// approve</c>, <c>h9k pr request-changes</c>, and <c>h9k review resolve --merge-ready</c> for a
/// review the owner posted by hand from the findings report. Which of those it was is not
/// re-derived here and does not matter: what the follow-through watches is the pull request, and
/// the pull request says the same thing whoever typed the review.
/// </para>
/// <para>
/// It carries no thread watermark, deliberately. Finalize has never read the pull request and
/// gains no gh call here — a read placed there would be an irreversible-adjacent failure surface
/// with nothing to retry it, and the honest place for one is the poll that already has retry,
/// backoff, and a per-task failure count
/// (<c>PullRequestReviewFollowThroughObserved</c> is the first thing that poll records). Until
/// that first observation lands, this task's own honest answer is "the review is posted and the
/// pull request has not been looked at yet", which is what <c>h9k status</c> says.
/// </para>
/// <para>
/// <see cref="RunId"/> is the run whose review this follows through on — the run that just
/// completed, kept so the scoped lap
/// (<c>h9k pr review --since-my-review</c>) can find the findings report the original review was
/// directed from, and so a reader of a waiting task can still see which run produced it.
/// </para>
/// </summary>
public sealed record PullRequestReviewFollowThroughOpened(
    Guid Id,
    Guid RunId,
    string PullRequestUrl,
    string? HeadSha,
    DateTimeOffset OpenedAt);
