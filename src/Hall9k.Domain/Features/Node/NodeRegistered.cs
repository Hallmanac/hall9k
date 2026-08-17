namespace Hall9k.Domain.Features.Node;

public sealed record NodeRegistered(
    Guid Id,
    Guid OwnerId,
    string MachineName,
    string OperatingSystem,
    DateTimeOffset RegisteredAt);
