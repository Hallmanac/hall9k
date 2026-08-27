using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A fix session was spawned in the run's worktree with the review findings of the given
/// cycle as its prompt (Decisions Log #23). A fresh headless session — the loop is
/// review → fix → gates → review, each leg with fresh context. One fix session per cycle
/// handles every still-active track's findings together; what bounds the loop is the per-track
/// cycle caps, not a count of fix runs (log #63). Model is the resolved model this fix session
/// was spawned on, resolved for the Fix role in its own right, since a fix session and a review
/// session have different shapes (log #33) — unless <paramref name="Escalated"/>, in which case
/// it is resolved for the Review role instead (task: a second fix round over the same findings):
/// a fix session dispatched over findings that repeat an earlier fix round's own
/// findings — automated, or a human's needs-fixes verdict restating them — gets the stronger
/// model exactly where the observed dodge-and-redo failure mode recurs.
/// <paramref name="EscalationReason"/> is non-null only when <paramref name="Escalated"/> is
/// true, and names why (the Daemon-layer review engine decides it — Domain only records it).
/// </summary>
public sealed record ReviewFixDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null,
    bool Escalated = false,
    string? EscalationReason = null);
