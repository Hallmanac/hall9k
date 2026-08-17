using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// OwnerId is the node's owner AT dispatch time — frozen so the accountability chain
/// (PLAN.md §6.2) survives any future node ownership change.
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
    DateTimeOffset DispatchedAt);
