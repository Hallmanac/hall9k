using Hall9k.Connectors.Credentials;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Builds providers out of registered connections (PLAN.md §10). It exists because the two
/// sources Hall9k speaks to need opposite things: GitHub piggybacks the machine's own <c>gh</c>
/// login and can be constructed from nothing, while Jira needs a site, an account, and a
/// credential reference that only the connection list knows — which is exactly the "one thing it
/// will add is construction" that decision #60 predicted the Jira provider would bring.
/// <para>
/// Every caller that reaches an external work item goes through here rather than newing a
/// provider up, so the rules about which connection is used, and what happens when none is
/// registered, are written once.
/// </para>
/// </summary>
public static class WorkItemConnections
{
    /// <summary>
    /// What every surface says when Jira is asked for on an install that has not connected it.
    /// One string because it is one answer: the import path, the publication path, and the link
    /// path all fail on the same missing connection, and an agent that learns the remedy from one
    /// of them should read the same sentence from the others.
    /// </summary>
    public const string NoJiraConnection =
        "No Jira connection is registered on this install, and Hall9k holds no Jira credentials "
        + "of its own (PLAN.md §10). Register one: h9k connection add jira --site "
        + "https://your-org.atlassian.net --email you@example.com";

    /// <summary>
    /// The importer for this install: the GitHub provider always, and the Jira provider when a
    /// Jira connection is registered. Without one, Jira is carried as an unregistered source
    /// rather than left out silently, so importing a card on a machine that has not connected
    /// Jira is refused with the command that connects it instead of with a list of the sources
    /// that happen to be configured.
    /// </summary>
    public static async Task<WorkItemImporter> ImporterAsync(
        IQuerySession session, CancellationToken cancellationToken, JiraRequester? requester = null)
    {
        JiraWorkItemProvider? jira = await TryJiraProviderAsync(session, cancellationToken, requester: requester);
        return jira is null
            ? new WorkItemImporter(new GitHubWorkItemProvider())
            {
                Unregistered = new Dictionary<WorkItemProvider, string>
                {
                    [WorkItemProvider.Jira] = NoJiraConnection,
                },
            }
            : new WorkItemImporter(new GitHubWorkItemProvider(), jira);
    }

    /// <summary>The Jira provider, or null when this install has no Jira connection registered.</summary>
    public static async Task<JiraWorkItemProvider?> TryJiraProviderAsync(
        IQuerySession session,
        CancellationToken cancellationToken,
        CredentialVault? vault = null,
        JiraRequester? requester = null)
    {
        ConnectionDetails? connection = await FindJiraConnectionAsync(session, cancellationToken);
        return connection is null ? null : Build(connection, vault, requester);
    }

    /// <summary>
    /// The Jira provider, or a refusal that says how to get one. Used by the commands whose whole
    /// purpose is Jira, where "no connection registered" is the answer the human needs rather
    /// than a null to fall through.
    /// </summary>
    public static async Task<JiraWorkItemProvider> JiraProviderAsync(
        IQuerySession session,
        CancellationToken cancellationToken,
        CredentialVault? vault = null,
        JiraRequester? requester = null)
    {
        ConnectionDetails connection = await FindJiraConnectionAsync(session, cancellationToken)
            ?? throw new DomainNotFoundException(NoJiraConnection);

        return Build(connection, vault, requester);
    }

    /// <summary>
    /// The Jira connection, refusing rather than choosing when there is more than one.
    /// <para>
    /// The connections model is a list precisely so several accounts per provider stay possible
    /// (PLAN.md §10), but nothing yet says which of two Jira accounts a given project uses: a
    /// project binds one connection, and that binding is its repository's. So v0 supports one
    /// Jira connection per install and says so out loud, rather than silently taking the oldest
    /// and reading somebody's cards as the wrong account. Binding a project to a second Jira
    /// account is the feature that closes this, and it is a decision about the project record
    /// rather than a fix here.
    /// </para>
    /// </summary>
    public static async Task<ConnectionDetails?> FindJiraConnectionAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        // WorkItemProvider is a value object and Marten cannot translate a comparison against
        // one, so the filter is SQL against the stored string — the house idiom for exactly this.
        IReadOnlyList<ConnectionDetails> connections = await session.Query<ConnectionDetails>()
            .Where(connection => connection.MatchesSql("d.data ->> 'provider' = ?", WorkItemProvider.Jira.Value))
            .OrderBy(connection => connection.RegisteredAt)
            .ToListAsync(cancellationToken);

        return connections.Count switch
        {
            0 => null,
            1 => connections[0],
            _ => throw new DomainConflictException(
                $"{connections.Count} Jira connections are registered and nothing says which one this "
                + "project uses, so Hall9k will not pick: "
                + string.Join(", ", connections.Select(Describe))
                + ". A project binds one connection and that binding is its repository's, so v0 supports "
                + "one Jira account per install. Keep the one you want."),
        };
    }

    /// <summary>
    /// A connection as a provider. The site is parsed rather than trusted: it was written to the
    /// event stream as a URL, and a connection registered before this field existed carries none
    /// at all — which is a Jira connection that cannot be used, and says so here rather than
    /// producing request URLs against a null host.
    /// </summary>
    private static JiraWorkItemProvider Build(
        ConnectionDetails connection, CredentialVault? vault, JiraRequester? requester) =>
        connection.SiteUrl is { } site
            ? new JiraWorkItemProvider(
                new JiraAccount(
                    site,
                    connection.ExternalAccountId,
                    CredentialReference.Parse(connection.CredentialReference),
                    vault),
                requester)
            : throw new DomainValidationException(
                $"The registered Jira connection ({Describe(connection)}) has no site recorded, so there "
                + "is nowhere to send a request. Register it again: h9k connection add jira --site "
                + "https://your-org.atlassian.net --email you@example.com");

    private static string Describe(ConnectionDetails connection) =>
        $"{connection.ExternalAccountId} at {connection.SiteUrl?.ToString() ?? "no site recorded"}";
}
