using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Connection;

public sealed class ConnectionDetails
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public WorkItemProvider Provider { get; set; } = WorkItemProvider.Unknown;
    public string ExternalAccountId { get; set; } = string.Empty;
    public string CredentialReference { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class ConnectionDetailsProjection : SingleStreamProjection<ConnectionDetails, Guid>
{
    public ConnectionDetails Create(IEvent<ConnectionRegistered> @event) => new()
    {
        Id = @event.Data.Id,
        OwnerId = @event.Data.OwnerId,
        Provider = @event.Data.Provider,
        ExternalAccountId = @event.Data.ExternalAccountId,
        CredentialReference = @event.Data.CredentialReference.ToString(),
        RegisteredAt = @event.Data.RegisteredAt,
    };
}
