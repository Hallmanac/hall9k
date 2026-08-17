namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record TaskFailed(
    Guid Id,
    Guid RunId,
    string Reason,
    DateTimeOffset FailedAt);
