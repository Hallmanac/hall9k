namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A session ended with meaningful uncommitted files still sitting in the worktree —
/// <see cref="VerificationFailed"/>'s own pre-gate check (backlog 57) — and the daemon is
/// about to spawn one bounded, commit-only session onto the SAME worktree instead of failing
/// the run outright and waiting for a human <c>h9k task retry</c> (task: when a session ends
/// with finished work uncommitted, the daemon recovers on its own). Origin: 2026-09-05, five
/// fix-lap sessions ended this way in one afternoon and every one was recovered by a human
/// running exactly that command by hand.
/// <para>
/// Recorded — and saved — before the recovery session is ever spawned, not after: a daemon
/// restart mid-wait must find this fact on the stream even though the spawn's own outcome is
/// still unknown, the same "save the decision before the wait" discipline commit 372acb38 fixed
/// for the session-error-retry leg. <see cref="StrandedFiles"/> and <see cref="Reason"/> are the
/// same pre-gate observation <see cref="RunFailed"/> would otherwise have carried — carried here
/// instead so the recovery session's own narrow prompt can name them without re-deriving
/// anything, and so a human reading the stream later sees exactly what the daemon saw.
/// </para>
/// <para>
/// At most one of these per run: a run whose projection already shows one never earns a
/// second, whatever happens next — a run whose OWN recovery session also ends dirty fails
/// exactly as before, naming that the recovery was tried.
/// </para>
/// </summary>
public sealed record RunUncommittedWorkRecoveryAttempted(
    Guid Id,
    Guid SessionId,
    IReadOnlyList<string> StrandedFiles,
    string Reason,
    DateTimeOffset AttemptedAt);
