namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A fix session was spawned in the run's worktree with the review findings of the given
/// cycle as its prompt (Decisions Log #23). A fresh headless session — the loop is
/// review → fix → gates → review, each leg with fresh context. Counted against
/// DaemonOptions.MaxAutomaticReviewFixRuns.
/// </summary>
public sealed record ReviewFixDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt);
