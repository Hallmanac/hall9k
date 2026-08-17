using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

public sealed record ConnectionRegistered(
    Guid Id,
    Guid OwnerId,
    WorkItemProvider Provider,
    string ExternalAccountId,
    CredentialReference CredentialReference,
    DateTimeOffset RegisteredAt);
