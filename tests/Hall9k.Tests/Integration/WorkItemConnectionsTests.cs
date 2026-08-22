using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Building the importer out of whatever connections this install has registered (PLAN.md §10),
/// including the ones it cannot use.
/// <para>
/// The question every test here asks is how far a Jira misconfiguration is allowed to reach.
/// GitHub piggybacks the machine's own <c>gh</c> login, needs no connection record, and cannot be
/// ambiguous, so a task whose reference is a GitHub issue must keep working while Jira is
/// unusable. Origin incident (2026-08-22): the pre-PR review of the Jira branch found two
/// registered Jira connections making h9k task show and h9k task add --from-issue exit non-zero
/// quoting a Jira refusal.
/// </para>
/// <para>
/// It needs the real projection because "which Jira connection" is a query over the connection
/// list rather than a comparison, and the fixture's database is shared by every test in the
/// class, so each one registers its connections and clears them again.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class WorkItemConnectionsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private const string GitHubIssue = "github:Hallmanac/hall9k#42";

    [Fact]
    public async Task An_ambiguous_jira_connection_refuses_jira_and_leaves_github_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await ClearConnectionsAsync(store, cts.Token);
        await RegisterAsync(store, "alice@corp.com", Site, cts.Token);
        await RegisterAsync(store, "bob@corp.com", Site, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().Be(
            new Uri("https://github.com/Hallmanac/hall9k/issues/42"),
            "GitHub needs no connection record and cannot be the ambiguous one");

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainConflictException>(
            "the ambiguity is still fatal to the commands that actually need Jira")).Which.Message
            .Should().Contain("2 Jira connections are registered")
            .And.Contain("alice@corp.com")
            .And.Contain("bob@corp.com")
            .And.NotContain("Known sources", "the sources that happen to be configured are not the answer");
    }

    /// <summary>
    /// The same degradation for the other way a Jira connection is unusable: one registered before
    /// the site was recorded on the event, which is a Jira connection with nowhere to send a
    /// request. The refusal keeps its own kind, because a connection that needs registering again
    /// is not the same failure as two nobody can choose between.
    /// </summary>
    [Fact]
    public async Task A_jira_connection_with_no_site_refuses_jira_and_leaves_github_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await ClearConnectionsAsync(store, cts.Token);
        await RegisterAsync(store, "alice@corp.com", site: null, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().NotBeNull();

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("no site recorded")
            .And.Contain("h9k connection add jira --site");
    }

    [Fact]
    public async Task An_install_with_no_jira_connection_is_told_the_command_that_connects_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await ClearConnectionsAsync(store, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().NotBeNull();

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("No Jira connection is registered")
            .And.Contain("h9k connection add jira --site");
    }

    private static Task<ImportedWorkItem> ImportAsync(
        WorkItemImporter importer, WorkItemProvider provider, CancellationToken cancellationToken) =>
        importer.ImportAsync(
            new WorkItemImportRequest(provider, "PROJ-123", Path.GetTempPath()), cancellationToken);

    /// <summary>
    /// A registration, appended as the event rather than through the decider when the site is
    /// null: the decider requires one, and the connection this asks about is the one written
    /// before the field existed.
    /// </summary>
    private static async Task RegisterAsync(
        DocumentStore store, string email, Uri? site, CancellationToken cancellationToken)
    {
        Guid connectionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(connectionId, new ConnectionRegistered(
            connectionId,
            DomainId.New(),
            WorkItemProvider.Jira,
            email,
            CredentialReference.EnvironmentVariable("JIRA_API_TOKEN"),
            Now,
            site));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task ClearConnectionsAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.DeleteWhere<ConnectionDetails>(connection => true);
        await session.SaveChangesAsync(cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
