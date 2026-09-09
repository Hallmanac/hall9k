namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// How one automatic uncommitted-work recovery session ended (task: when a session ends
/// with finished work uncommitted, the daemon recovers on its own) — recorded from a FRESH
/// re-detection of the worktree, never the session's own self-report, the same ground-truth
/// discipline <c>VerificationRunner.RecoverUncommittedWorkOrExplainAsync</c> already applies to
/// the failure reason it returns. Gives <c>h9k task show</c> a real outcome to read instead of
/// inferring one from whatever the run's own state happens to be later, which conflated an
/// unrelated downstream gate failure with a failed recovery (independent pre-PR review, cycle 1,
/// both lenses). Absent on the matching entry in <c>RunDetails.UncommittedWorkRecoveries</c> until
/// this appends — a daemon restart between the attempt and this completion leaves the outcome
/// honestly unknown rather than guessed.
/// <para>
/// <see cref="RecoveredCleanly"/> is null, not a guessed <c>true</c>, when the re-detection
/// itself could not read the worktree's `git status` — an unobserved tree is not the same fact as
/// an observed clean one (independent pre-PR review, cycle 1, conformance finding).
/// <see cref="DiscardedFiles"/> names every originally-stranded file that stopped showing up as
/// dirty without ever actually landing in a commit — reverted, deleted, or silently rewritten
/// instead of committed as-is — so a recovery that discarded finished work is recorded as exactly
/// that rather than as a silent success (same finding).
/// </para>
/// </summary>
public sealed record RunUncommittedWorkRecoveryCompleted(
    Guid Id,
    bool? RecoveredCleanly,
    IReadOnlyList<string> DiscardedFiles,
    DateTimeOffset CompletedAt);
