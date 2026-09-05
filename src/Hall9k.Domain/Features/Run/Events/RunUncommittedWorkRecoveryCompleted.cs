namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// How the one automatic uncommitted-work recovery session ended (task: when a session ends
/// with finished work uncommitted, the daemon recovers on its own) — recorded from a FRESH
/// re-detection of the worktree, never the session's own self-report, the same ground-truth
/// discipline <c>VerificationRunner.RecoverUncommittedWorkOrExplainAsync</c> already applies to
/// the failure reason it returns. Gives <c>h9k task show</c> a real outcome to read instead of
/// inferring one from whatever the run's own state happens to be later, which conflated an
/// unrelated downstream gate failure with a failed recovery (independent pre-PR review, cycle 1,
/// both lenses). Absent on <c>RunDetails.UncommittedWorkRecovery</c> until this appends — a
/// daemon restart between the attempt and this completion leaves the outcome honestly unknown
/// rather than guessed.
/// </summary>
public sealed record RunUncommittedWorkRecoveryCompleted(
    Guid Id,
    bool RecoveredCleanly,
    DateTimeOffset CompletedAt);
