namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Queued or Blocked -> Published: the reverse of the dispatch trigger (Decisions Log #34).
/// Refused while a lease is held — revising work a node is already running races the
/// dispatcher, which is the whole reason editing stops at Draft.
/// </summary>
public sealed record TaskUnassigned(
    Guid Id,
    string? Reason,
    DateTimeOffset UnassignedAt,
    Guid UnassignedByOwnerId);
