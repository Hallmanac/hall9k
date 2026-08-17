namespace Hall9k.Domain.Features.Run.Events;

public sealed record PullRequestOpened(
    Guid Id,
    string PullRequestUrl,
    int PullRequestNumber,
    DateTimeOffset OpenedAt);
