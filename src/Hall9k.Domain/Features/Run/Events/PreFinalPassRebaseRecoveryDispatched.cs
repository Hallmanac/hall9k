using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A plain <c>git rebase</c> onto the base branch conflicted immediately before the mandatory
/// final full pass (task: a run rebases its branch onto the current base branch), so a narrow
/// recovery session was spawned in the run's own retained worktree — the rebase-onto-main
/// skill's own mechanics, dispatched inside this same run rather than through a task reopen
/// (Decisions Log #135's "inside the same run" requirement): it does not fail the run and it
/// does not reopen the task. The worktree was already restored to its pre-conflict tip
/// (<c>git rebase --abort</c>) before this session was spawned, so it starts from a clean
/// checkout and redoes the fetch and rebase itself, exactly like the existing rebase-onto-main
/// skill already does for a post-PR follow-up.
/// </summary>
/// <param name="RebasedFromCommit">The base commit this branch was built against before this attempt.</param>
/// <param name="RebasedOntoCommit">The base branch's tip this attempt is rebasing onto.</param>
/// <param name="PrecedesFirstReviewCycle">
/// True only when this session was dispatched from a stacked checkpoint's own replay retry
/// (<c>ReviewEngine.ActOnStackAssessmentAsync</c>'s <c>Replay</c> branch) that precedes this run's
/// first review cycle (<c>StackedCheckpoint.BeforeFirstReviewCycle</c>) — read back by
/// <see cref="RunAggregate.Apply(PreFinalPassRebaseRecoveryCompleted)"/> to land a non-disputed
/// completion on <c>ReviewPhase.Reverify</c> (so the review cycles this checkpoint precedes still
/// run) rather than <c>ReviewPhase.Settling</c>, the ordinary pre-final-pass and
/// <c>StackedCheckpoint.BeforeFinalPass</c> destination. False for every other dispatch, including
/// a redispatch of an already-checkpoint-originated track (carried forward from
/// <see cref="RunAggregate.RebaseRecoveryPrecedesFirstReviewCycle"/> rather than recomputed).
/// </param>
/// <param name="BaseCommit">
/// The stacked parent's fork point this session should replay from, when one applies — the
/// assessment verdict's own <c>BoundaryCommit</c> on a fresh checkpoint-originated dispatch, or the
/// same track's carried-forward <see cref="RunAggregate.RebaseRecoveryBaseCommit"/> on a
/// human-resolved redispatch. Null for the ordinary, unstacked pre-final-pass path, which needs no
/// fork point at all (<see cref="Hall9k.Daemon.Execution.AgentPromptBuilder.BuildPreFinalPassRebase"/>'s
/// own <c>isStacked</c> check is false whenever this is null or the run is not currently stacked).
/// </param>
public sealed record PreFinalPassRebaseRecoveryDispatched(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model,
    string RebasedFromCommit,
    string RebasedOntoCommit,
    string SessionName,
    bool PrecedesFirstReviewCycle = false,
    string? BaseCommit = null);
