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
    /// <summary>
    /// The identity the tracker itself reports these credentials as — Jira's own
    /// <c>accountId</c> — or null when nothing has observed it yet (a connection registered
    /// before <see cref="ConnectionTrackerIdentityObserved"/> existed). Never typed, never an
    /// email or a display name.
    /// </summary>
    public string? TrackerAccountId { get; private set; }
    /// <summary>
    /// GitHub's own numeric id for this connection's account, or null when it has never been
    /// observed — see <see cref="ConnectionGitHubIdentityObserved"/>. Never set for a Jira
    /// connection.
    /// </summary>
    public long? GitHubAccountId { get; private set; }
    /// <summary>The login GitHub reported alongside <see cref="GitHubAccountId"/> at the same observation.</summary>
    public string? GitHubLogin { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }
    /// <summary>The email <c>claude auth status</c> last reported for a claude-code connection's directory.</summary>
    public string? ClaudeCodeEmail { get; private set; }
    public string? ClaudeCodeOrganizationId { get; private set; }
    public string? ClaudeCodeOrganizationName { get; private set; }
    public string? ClaudeCodeSubscriptionType { get; private set; }
    /// <summary>Whether this claude-code connection's last check failed and nothing may start under it.</summary>
    public bool ClaudeCodeRefused { get; private set; }

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

    public void Apply(ConnectionTrackerIdentityObserved @event) => TrackerAccountId = @event.TrackerAccountId;

    public void Apply(ConnectionGitHubIdentityObserved @event)
    {
        GitHubAccountId = @event.GitHubAccountId;
        GitHubLogin = @event.GitHubLogin;
    }

    public void Apply(ConnectionClaudeCodeAccountObserved @event)
    {
        ClaudeCodeEmail = @event.Email;
        ClaudeCodeOrganizationId = @event.OrganizationId;
        ClaudeCodeOrganizationName = @event.OrganizationName;
        ClaudeCodeSubscriptionType = @event.SubscriptionType;
    }

    public void Apply(ConnectionClaudeCodeAccountRefused @event) => ClaudeCodeRefused = true;

    public void Apply(ConnectionClaudeCodeAccountCleared @event) => ClaudeCodeRefused = false;
}
