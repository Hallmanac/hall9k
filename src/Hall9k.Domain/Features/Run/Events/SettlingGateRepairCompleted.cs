namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The narrow Settling-gate repair session finished (task: a pre-final-pass rebase that applies
/// cleanly but breaks the mandatory gate gets a repair lap inside the same run instead of failing
/// it). Unlike <see cref="PreFinalPassRebaseRecoveryCompleted"/>, this carries no outcome of its
/// own to trust or distrust: the session's own claim is never the judge of whether the repair
/// worked, only the mandatory gate is — this event always routes the loop back to
/// <see cref="ReviewPhase.Settling"/>, whose own re-entry runs that gate again over whatever the
/// session left behind. A gate that passes there settles the round; a gate that fails again
/// dispatches another one (or parks, once the round cap is spent) — see
/// <c>ReviewEngine.DispatchSettlingGateRepairSessionAsync</c>'s own cap check, the one place that
/// decision is made.
/// </summary>
public sealed record SettlingGateRepairCompleted(Guid Id, DateTimeOffset CompletedAt);
