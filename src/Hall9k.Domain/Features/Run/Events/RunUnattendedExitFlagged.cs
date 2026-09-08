namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A deliberate headless start (<c>h9k task start</c>) exited with nobody watching, and what the
/// platform found was not one it could honestly deliver on the operator's behalf — uncommitted
/// files, no commits beyond the base branch, the wrong branch checked out, git itself unobservable
/// at exit, the process vanishing without ever reporting a result, or the agent reporting a plain
/// error (origin incident 2026-09-05, task ef2fefe5). Recorded so the task reads needs-you within
/// seconds instead of "building" indefinitely, naming what was found
/// (<see cref="RunDetails.ExitedUnattendedReason"/>, what <c>TaskPhaseComposer</c> and
/// <c>AttentionComposer</c> read to say so) and whether <c>h9k task deliver</c> is confirmed to
/// refuse on that same ground (<see cref="DeliverConfirmedRefuses"/>) — true only for a
/// definitively bad tree (uncommitted files, or no commits beyond base — the identical
/// <c>VerificationRunner.DetectStrandedWorkAsync</c> check <c>h9k task deliver</c> itself runs) or
/// a confirmed branch mismatch (<c>TaskDeliverCommand</c>'s own <c>currentBranch != run.Branch</c>
/// check); false whenever deliver is not guaranteed to refuse outright — an unreadable branch or
/// an unreadable git status, both of which <c>TaskDeliverCommand</c> only warns about and
/// proceeds past rather than refusing on, and a process that died or reported a plain error
/// without the tree ever being checked at all (independent pre-PR review, cycle 1, both lenses:
/// a blanket "never advise deliver" contradicted the two variants where deliver does not, in
/// fact, refuse). Deliberately does not move <see cref="RunState"/>: the run stays exactly where
/// a live <c>h9k task work</c>/<c>h9k task handback</c>/<c>h9k task release</c> already expects
/// it (Dispatched or Running), so none of those three levers needs a widened guard to keep
/// working once this is recorded.
/// </summary>
public sealed record RunUnattendedExitFlagged(Guid Id, string Reason, DateTimeOffset FlaggedAt, bool DeliverConfirmedRefuses);
