namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Published -> Queued (or Blocked): the dispatch trigger, and always an explicit human act
/// (Decisions Log #34). Assignment is the only way a task becomes claimable, and the claim
/// guard reads <see cref="AssignedOwnerId"/> — a node claims only its own owner's work.
/// <see cref="UnmetDependencies"/> is the dependency set as observed at assignment time:
/// empty means Queued, anything else means Blocked until each one reaches true closeout.
/// </summary>
public sealed record TaskAssigned(
    Guid Id,
    Guid AssignedOwnerId,
    IReadOnlyList<Guid> UnmetDependencies,
    DateTimeOffset AssignedAt,
    Guid AssignedByOwnerId);
