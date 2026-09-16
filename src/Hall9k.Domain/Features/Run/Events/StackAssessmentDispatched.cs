using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A stacked checkpoint or the pre-final-pass rebase hit a git shape it could not decide
/// mechanically (a replay conflict, a parent merged elsewhere, an undecidable boundary, a buried
/// or colliding Decisions Log placeholder, a push guard refusal) and, before parking for a
/// human, dispatched its one read-only assessment run — a narrow, turn-capped session that
/// inspects the real worktree and GitHub and reports a structured verdict rather than acting on
/// anything itself.
/// <para>
/// Recorded before the spawn, the same "save the decision before the wait" discipline
/// <see cref="RunUncommittedWorkRecoveryAttempted"/> follows: a run earns at most one of these
/// across its whole lifetime (<see cref="StackAssessmentCompleted"/>'s own doc), so this event
/// alone is what a later park on the same run reads to know an assessment already ran, even if
/// the daemon restarts before the session's own result comes back.
/// </para>
/// </summary>
/// <param name="Id">The run this assessment runs inside.</param>
/// <param name="ParkKind">
/// Which git shape this would otherwise have parked for — <c>StackAssessmentParkKind</c>'s own
/// value (a closed vocabulary, carried here as a plain string the way every other closed
/// vocabulary lands on an event).
/// </param>
/// <param name="ChildBranch">The branch being assessed.</param>
/// <param name="ParentBranch">The branch it is stacked on, blank when this is the pre-final-pass rebase rather than a stacked checkpoint.</param>
/// <param name="RecordedForkPoint">This run's own recorded fork point at dispatch time — the platform's belief, not yet verified.</param>
/// <param name="AttemptedOntoCommit">The commit a mechanical replay most recently attempted, or was about to attempt, to land onto.</param>
public sealed record StackAssessmentDispatched(
    Guid Id,
    string ParkKind,
    string ChildBranch,
    string ParentBranch,
    string RecordedForkPoint,
    string AttemptedOntoCommit,
    Guid SessionId,
    AgentModel? Model,
    DateTimeOffset DispatchedAt,
    string SessionName);
