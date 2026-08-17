namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record TaskAbandoned(
    Guid Id,
    string? Reason,
    DateTimeOffset AbandonedAt,
    Guid AbandonedByOwnerId);
