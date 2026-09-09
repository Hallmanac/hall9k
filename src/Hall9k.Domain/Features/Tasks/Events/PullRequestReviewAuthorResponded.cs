namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The pull request this task is following a review through on answered it — replies in the
/// reviewer's own threads, new commits pushed, a re-review newly requested of them, or any
/// combination (task: a pr-review task stays open while the
/// pull request's review threads are unresolved). Moves the task to
/// <see cref="TaskState.NeedsHuman"/>: the ball is back in the reviewer's court, so the board
/// says needs-you and names what changed.
/// <para>
/// Named for the author because that is who answers a review on an ordinary pull request, and the
/// state it leaves behind is <see cref="TaskState.AwaitingAuthor"/>'s counterpart — but what the
/// watch actually observed is narrower, and <see cref="Summary"/> says only that: a comment the
/// reviewer did not write arrived in a thread of theirs. A teammate or a bot writes one just as
/// legitimately, and the line never claims to know which (AGENTS.md: never guess at unobserved
/// facts).
/// </para>
/// <para>
/// Always appended in the same transaction as the
/// <see cref="PullRequestReviewFollowThroughObserved"/> that re-baselines the watermark, and
/// after it. That ordering is the whole reason this event carries deltas and no absolute counts:
/// the observation beside it holds the new watermark, so the same replies can never fire a second
/// notification on the next poll, and this event stays a record of one moment's news rather than
/// a second, competing copy of the pull request's state.
/// </para>
/// <para>
/// <see cref="Summary"/> is the one line every surface shows — <c>h9k status</c>, <c>h9k task
/// show</c>, the daemon's own log — composed once here so they cannot word it differently.
/// <see cref="InteractiveSessionAddress"/> is the reviewer's registered interactive session name
/// as this observation found it (<c>RunDetails.RegisteredInteractiveSessionName</c> on the run the
/// review rode on), or null when no session was ever registered against it. It is recorded as the
/// address the line was FOR, not as a claim that anything was delivered to it: the daemon has no
/// channel that reaches a live Claude Code session — in this platform an agent sends and the
/// daemon does not (ORCHESTRATOR-WINDOW.md's R5) — so what a registered session gets is this same
/// line off the board it already reads, under the task's own needs-you row.
/// </para>
/// </summary>
/// <param name="ReplyCount">How many comments the reviewer did not write landed in their own threads since the last observation.</param>
/// <param name="ThreadsWithReplies">How many distinct threads those replies landed in.</param>
/// <param name="NewCommitCount">
/// How many commits the pull request gained since the last observation, or null when the provider
/// reported no commit count to compare — a push that is visible in a moved head and whose size is
/// genuinely unknown, stated as unknown rather than reported as zero.
/// </param>
/// <param name="ReReviewNewlyRequested">
/// Whether this wake was, wholly or partly, the author asking the reviewer back: a review request
/// standing against them that the previous observation did not see. The only one of these four that
/// is an explicit ask rather than movement to interpret, and it wakes the reviewer on its own —
/// an author who resolves the threads themselves and re-requests review without a word or a push
/// has said "your turn", and a board still reading "waiting on its author" there left both of them
/// waiting on the other (independent pre-PR review, cycle 1, adversarial lens). A stream written
/// before this field existed deserializes it false, which is honest: no wake back then was ever a
/// re-review request, because none could be.
/// </param>
public sealed record PullRequestReviewAuthorResponded(
    Guid Id,
    string Summary,
    int ReplyCount,
    int ThreadsWithReplies,
    int? NewCommitCount,
    bool HeadMoved,
    bool ReReviewNewlyRequested,
    string? InteractiveSessionAddress,
    DateTimeOffset ObservedAt);
