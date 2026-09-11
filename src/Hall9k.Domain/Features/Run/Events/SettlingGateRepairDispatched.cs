using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The Settling phase's own mandatory gate failed on a tree whose most recent recorded
/// <see cref="RunRebasedOntoBase"/> was a real rebase (clean or recovered, never a no-op) — task:
/// a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a repair lap
/// inside the same run instead of failing it. A narrow session is dispatched, inside this same run
/// exactly like <see cref="PreFinalPassRebaseRecoveryDispatched"/> — no task reopen — carrying the
/// gate's own output rather than a git conflict, over the SAME <see cref="RunSessionLeg.RebaseRecovery"/>
/// leg and dispatch path that mechanism already built (the task's own smallest-shape ruling): only
/// the prompt and the cap/park reason are this feature's own.
/// </summary>
public sealed record SettlingGateRepairDispatched(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model,
    string GateOutput,
    string SessionName);
