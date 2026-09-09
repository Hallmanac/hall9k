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
/// At most one of these per (run, leg): a run whose projection already shows one for
/// <see cref="Leg"/> never earns a second one on that same leg, whatever happens next — a run
/// whose OWN recovery session also ends dirty fails exactly as before, naming that the recovery
/// was tried. Scoped to the leg, not the whole run (task: a headless build, fix, or recovery
/// session never ends its turn while a gate it started is still running in the background — the
/// uncommitted-work half of that task): a run's build leg spending the run's only automatic
/// recovery left a later human-resolved-fix leg on the very same run with no recovery of its own
/// and no honest way to tell "already tried and failed" from "never offered one at all" — the run
/// simply failed before its gates, twice, in production (origin incidents 2026-09-07/08). Per-leg
/// scoping is still bounded, deliberately: a second cycle of the SAME leg (a Fix leg's second
/// review-fix round, say) does not earn a second attempt either, on the same one-shot-per-thing
/// reasoning the original per-run design had — just narrowed to match what a session's own dirty
/// worktree actually has to do with, which is which leg produced it, not which run it happened on.
/// </para>
/// <para>
/// <see cref="Leg"/> is nullable and appended last, the same evolution shape every other field
/// added to an existing event on this stream uses (<c>ReviewDispatched.Lens</c>,
/// <c>ReviewPassCompleted.Mode</c>): a stream written before this field existed carries none, and
/// <see cref="Projections.RunDetailsProjection"/> reads a missing value as
/// <see cref="RunSessionLeg.Unknown"/> — the one leg no live eligibility check ever asks about, so
/// an old attempt neither blocks nor grants a fresh one on any real leg.
/// </para>
/// </summary>
public sealed record RunUncommittedWorkRecoveryAttempted(
    Guid Id,
    Guid SessionId,
    IReadOnlyList<string> StrandedFiles,
    string Reason,
    DateTimeOffset AttemptedAt,
    RunSessionLeg? Leg = null);
