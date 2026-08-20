using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// OwnerId is the node's owner AT dispatch time — frozen so the accountability chain
/// (PLAN.md §6.2) survives any future node ownership change.
/// Model is the resolved model this run's build session was spawned on, recorded as an
/// observed fact (Decisions Log #33), appended with a default so streams written before
/// the chain existed replay as Unknown rather than as a reconstruction (the log #30
/// discipline: the unobserved is admitted, never guessed).
/// </summary>
public sealed record RunDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid SessionId,
    string WorktreePath,
    string Branch,
    ExecutorMode ExecutorMode,
    DateTimeOffset DispatchedAt,
    bool IsFollowUp = false,
    AgentModel? Model = null);
