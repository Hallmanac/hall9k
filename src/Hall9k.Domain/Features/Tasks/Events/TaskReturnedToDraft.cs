namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Published -> Draft: the explicit revert that reopens a task for revision (Decisions Log
/// #34). Refused from Queued and Blocked onward — the edit-after-the-fact path is
/// unassign -> draft -> revise -> publish -> assign, and every step is its own act.
/// </summary>
public sealed record TaskReturnedToDraft(
    Guid Id,
    string? Reason,
    DateTimeOffset ReturnedAt,
    Guid ReturnedByOwnerId);
