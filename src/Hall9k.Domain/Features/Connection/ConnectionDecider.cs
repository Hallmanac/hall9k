using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

public static class ConnectionDecider
{
    public static ConnectionRegistered Register(
        Guid id,
        Guid ownerId,
        WorkItemProvider provider,
        string externalAccountId,
        CredentialReference credentialReference,
        DateTimeOffset registeredAt)
    {
        if (provider == WorkItemProvider.Unknown)
        {
            throw new DomainValidationException("A connection requires a known provider (e.g. github).");
        }

        if (externalAccountId.IsBlank())
        {
            throw new DomainValidationException("A connection requires the external account id it authenticates as.");
        }

        return new ConnectionRegistered(id, ownerId, provider, externalAccountId, credentialReference, registeredAt);
    }
}
