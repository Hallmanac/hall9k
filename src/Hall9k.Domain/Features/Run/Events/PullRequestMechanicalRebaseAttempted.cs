namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Closeout's mechanical fast path for a branch-conflicts-with-base obstruction (recommendation 3,
/// idea fc85f609): before ever reopening the task for a full agent review lap, the daemon attempts
/// a plain git fetch + rebase onto origin/&lt;base&gt; in the run's own retained worktree, no model
/// session and no local gates involved — GitHub's CI is the authoritative gate at the merge bar
/// (Brian, 2026-09-04), so a local build/test run here would only duplicate it. A clean apply is
/// force-pushed with the same ancestor-or-reflog lease guard every other push here already carries
/// (Decisions Log #104), and the task is never reopened for it. Anything else — the rebase itself
/// conflicting, the retained worktree missing or unusable, or the push being refused — falls back
/// byte-for-byte to today's full reopen-and-review lap: this event is still appended first, so the
/// attempt and why it fell back are on the record either way, immediately followed in the same
/// transaction by the identical <see cref="PullRequestConflictObserved"/> the engine has always
/// appended on this path.
/// <para>
/// Informational only: it never moves <see cref="RunState"/> on its own. A clean success leaves
/// the run exactly where it was (AwaitingReview), so the very next sweep re-inspects the new head
/// with no extra wiring — GitHub's own check run is the validation, not a local gate here.
/// </para>
/// </summary>
/// <param name="Succeeded">Whether the rebase applied cleanly onto origin/&lt;base&gt; and was pushed.</param>
/// <param name="Detail">
/// What actually happened, readable from <c>h9k task show</c>: which git step came up short, or
/// what a clean push's new head commit was.
/// </param>
/// <param name="PushedCommit">The branch's new head after a clean force-push; null on any fallback.</param>
public sealed record PullRequestMechanicalRebaseAttempted(
    Guid Id,
    bool Succeeded,
    string Detail,
    string? PushedCommit,
    DateTimeOffset AttemptedAt);
