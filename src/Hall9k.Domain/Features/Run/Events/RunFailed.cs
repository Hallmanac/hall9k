namespace Hall9k.Domain.Features.Run.Events;

public sealed record RunFailed(
    Guid Id,
    string Reason,
    DateTimeOffset FailedAt);
