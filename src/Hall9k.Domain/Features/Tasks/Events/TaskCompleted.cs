namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record TaskCompleted(
    Guid Id,
    Guid RunId,
    string? PullRequestUrl,
    DateTimeOffset CompletedAt);
