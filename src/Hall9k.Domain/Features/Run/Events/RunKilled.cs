using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Run.Events;

public sealed record RunKilled(
    Guid Id,
    KillReason Reason,
    Guid? KilledByOwnerId,
    DateTimeOffset KilledAt);
