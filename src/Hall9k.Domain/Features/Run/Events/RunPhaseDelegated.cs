namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// An operator holding a task interactively (<c>h9k task work</c>) dispatched a contractor build
/// agent to do this one phase while staying the arbiter (<c>h9k task delegate</c>, task
/// 15f889e3-h9k, design ruling R6, idea fcaded0b's design rulings, Take the Wheel epic 9272e514's
/// slice 10) — distinct from <c>h9k task handback</c>, which ends interactive mode and hands the
/// task back to the machine, this reuses the same run and worktree the interactive claim already
/// holds and never touches the task's claim, its assignment, or its interactive-mode flag.
/// </summary>
/// <param name="Id">The run this contractor session was dispatched against — the interactive claim's own run.</param>
/// <param name="Note">
/// The operator's own handoff, in the blocker-handoff mold: what was attempted, what is
/// deliberate versus abandoned, what latitude is granted. Carried verbatim into the contractor's
/// starting prompt and recorded here so the decision to delegate, and why, survives this session.
/// </param>
public sealed record RunPhaseDelegated(
    Guid Id, string Note, DateTimeOffset DelegatedAt, Guid DelegatedByOwnerId, string SessionName);
