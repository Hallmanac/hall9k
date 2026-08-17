namespace Hall9k.Domain.Features.Project.Events;

public sealed record ProjectRegistered(
    Guid Id,
    Guid OwnerId,
    Guid ConnectionId,
    string Name,
    string RepositoryPath,
    Uri? RepositoryUrl,
    string BaseBranch,
    DateTimeOffset RegisteredAt);
