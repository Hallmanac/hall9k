namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// One look at the pull request a posted review is waiting on (task: a pr-review task stays open
/// while the pull request's review threads are unresolved) — the closeout watcher's own poll,
/// recorded as the observation it is. State is never touched here: this is the watermark a LATER
/// poll compares against, and what moves the task is either
/// <see cref="PullRequestReviewAuthorResponded"/> (the pull request answered) or an ordinary
/// <see cref="TaskCompleted"/> (the follow-through is over).
/// <para>
/// Appended on every poll that sees something different from the last one, and skipped entirely
/// when nothing changed: a quiet pull request polled every few minutes for a week must not write
/// a thousand identical observations onto the task's own stream.
/// </para>
/// <para>
/// <see cref="ReviewerLogin"/> is the login the poll read back from <c>gh</c> that same sweep,
/// never a configured or remembered name — the same discipline the auto-pr-review sweep already
/// keeps for the same reason: it is the account whose threads this follow-through is about, and a
/// stale copy of it would watch the wrong person's comments. <see cref="Threads"/> is that
/// login's own threads on the pull request and nothing else; another reviewer's unresolved thread
/// is somebody else's conversation and never holds this task open.
/// </para>
/// <para>
/// <see cref="CommitCount"/> is honestly null where the provider reported no count for it
/// (AGENTS.md: the unobserved is represented as explicitly unknown), which costs the next poll
/// its "new commits" number and nothing else — the reply half of the comparison still works, and
/// a moved <see cref="HeadSha"/> still says a push happened even when the count of it cannot
/// be stated.
/// </para>
/// </summary>
public sealed record PullRequestReviewFollowThroughObserved(
    Guid Id,
    string ReviewerLogin,
    IReadOnlyList<PrReviewThreadWatermark> Threads,
    bool ReReviewRequested,
    string? HeadSha,
    int? CommitCount,
    DateTimeOffset ObservedAt);
