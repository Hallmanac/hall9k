using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A fix session was spawned in the run's worktree with the review findings of the given
/// cycle as its prompt (Decisions Log #23). A fresh headless session — the loop is
/// review → fix → gates → review, each leg with fresh context. One fix session per cycle
/// handles every still-active track's findings together; what bounds the loop is the per-track
/// cycle caps, not a count of fix runs (log #63). Model is the resolved model this fix session
/// was spawned on, resolved for the Fix role in its own right, since a fix session and a review
/// session have different shapes (log #33).
/// </summary>
public sealed record ReviewFixDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);
