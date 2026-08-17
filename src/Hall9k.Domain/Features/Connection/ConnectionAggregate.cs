using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

public sealed class ConnectionAggregate
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public WorkItemProvider Provider { get; private set; } = WorkItemProvider.Unknown;
    public string ExternalAccountId { get; private set; } = string.Empty;
    public CredentialReference CredentialReference { get; private set; } = CredentialReference.GhCli;
    public DateTimeOffset RegisteredAt { get; private set; }

    public void Apply(ConnectionRegistered @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        Provider = @event.Provider;
        ExternalAccountId = @event.ExternalAccountId;
        CredentialReference = @event.CredentialReference;
        RegisteredAt = @event.RegisteredAt;
    }
}
