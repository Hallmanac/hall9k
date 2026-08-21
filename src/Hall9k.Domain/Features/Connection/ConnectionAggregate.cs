using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

public sealed class ConnectionAggregate
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public WorkItemProvider Provider { get; private set; } = WorkItemProvider.Unknown;
    public string ExternalAccountId { get; private set; } = string.Empty;
    public CredentialReference CredentialReference { get; private set; } = CredentialReference.GhCli;
    /// <summary>The tenant this account lives at; null for providers with exactly one home (PLAN.md §10).</summary>
    public Uri? SiteUrl { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }

    public void Apply(ConnectionRegistered @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        Provider = @event.Provider;
        ExternalAccountId = @event.ExternalAccountId;
        CredentialReference = @event.CredentialReference;
        SiteUrl = @event.SiteUrl;
        RegisteredAt = @event.RegisteredAt;
    }

    // Identity and ownership are the two things a re-registration cannot change: projects bind
    // to this id, and whose connection it is was settled when it was created.
    public void Apply(ConnectionReregistered @event)
    {
        Provider = @event.Provider;
        ExternalAccountId = @event.ExternalAccountId;
        CredentialReference = @event.CredentialReference;
        SiteUrl = @event.SiteUrl;
    }
}
