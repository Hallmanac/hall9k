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
public sealed record PreFinalPassRebaseRecoveryDispatched(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model,
    string RebasedFromCommit,
    string RebasedOntoCommit,
    string SessionName);
