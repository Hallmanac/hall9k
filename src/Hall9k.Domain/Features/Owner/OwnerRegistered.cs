namespace Hall9k.Domain.Features.Owner;

public sealed record OwnerRegistered(
    Guid Id,
    string Name,
    string? Email,
    DateTimeOffset RegisteredAt);
