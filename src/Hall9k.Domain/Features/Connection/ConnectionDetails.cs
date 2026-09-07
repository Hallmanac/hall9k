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
    /// <summary>The tenant this account lives at; null for providers with exactly one home.</summary>
    public Uri? SiteUrl { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    /// <summary>When the credentials or the site were last replaced; null while still as first registered.</summary>
    public DateTimeOffset? ReregisteredAt { get; set; }
    /// <summary>
    /// The identity the tracker itself reports these credentials as (Jira's own <c>accountId</c>),
    /// or null when nothing has observed it yet — see <see cref="ConnectionTrackerIdentityObserved"/>.
    /// </summary>
    public string? TrackerAccountId { get; set; }
    /// <summary>When <see cref="TrackerAccountId"/> was read from the tracker; null while it is unobserved.</summary>
    public DateTimeOffset? TrackerIdentityObservedAt { get; set; }
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
        SiteUrl = @event.Data.SiteUrl,
        RegisteredAt = @event.Data.RegisteredAt,
    };

    public void Apply(IEvent<ConnectionReregistered> @event, ConnectionDetails view)
    {
        view.Provider = @event.Data.Provider;
        view.ExternalAccountId = @event.Data.ExternalAccountId;
        view.CredentialReference = @event.Data.CredentialReference.ToString();
        view.SiteUrl = @event.Data.SiteUrl;
        view.ReregisteredAt = @event.Data.ReregisteredAt;
    }

    public void Apply(IEvent<ConnectionTrackerIdentityObserved> @event, ConnectionDetails view)
    {
        view.TrackerAccountId = @event.Data.TrackerAccountId;
        view.TrackerIdentityObservedAt = @event.Data.ObservedAt;
    }
}
