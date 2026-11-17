using FluentAssertions;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.ClaudeAccounts;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The claude-code connection reads against a real store. They filter on the provider, a value object Marten
/// cannot translate in a LINQ comparison, so only a real query proves the filter is written the way the store
/// can run it, and that the refusal ledger records and clears an account the daemon's monitor then sees.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ClaudeCodeAccountStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly PostgresFixture _postgres;

    public ClaudeCodeAccountStoreTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> RegisterAsync(
        WorkItemProvider provider, string account, CredentialReference credential, Uri? siteUrl = null)
    {
        Guid id = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(
            id, ConnectionDecider.Register(id, DomainId.New(), provider, account, credential, Now, siteUrl));
        await session.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Only_claude_code_connections_are_listed_and_a_refusal_is_recorded_then_cleared()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        string directory = Path.Join(Path.GetTempPath(), "h9k-store-test", ".claude-alt");
        Guid claudeCode = await RegisterAsync(WorkItemProvider.ClaudeCode, directory, CredentialReference.ClaudeConfigDirectory);
        await RegisterAsync(WorkItemProvider.GitHub, "someone", CredentialReference.GhCli);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            (await ClaudeCodeAccounts.ListAsync(query, cts.Token)).Select(connection => connection.Id)
                .Should().Equal(claudeCode);
            (await ClaudeAccountMonitor.RefusedConnectionsAsync(query, cts.Token)).Should().BeEmpty();
        }

        MartenClaudeAccountRefusalLedger ledger = new(_postgres.Store, NullLogger<MartenClaudeAccountRefusalLedger>.Instance);
        ClaudeAccountCheckResult refused = new(ClaudeAccountCheckOutcome.LoggedOut, null, "not logged in");
        await ledger.RecordAsync(claudeCode, refused, cts.Token);
        await ledger.RecordAsync(claudeCode, refused, cts.Token);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            (await ClaudeAccountMonitor.RefusedConnectionsAsync(query, cts.Token)).Select(connection => connection.Id)
                .Should().Equal(claudeCode);
            (await query.Events.FetchStreamAsync(claudeCode, token: cts.Token))
                .Count(@event => @event.Data is ConnectionClaudeCodeAccountRefused)
                .Should().Be(1, "the same refusal recorded twice is one event");
        }

        ClaudeAccountCheckResult passed = new(
            ClaudeAccountCheckOutcome.Passed,
            new ClaudeAuthStatus(true, "claude.ai", "dev@example.com", "org-1", null, null, null),
            "passed");
        await ledger.RecordAsync(claudeCode, passed, cts.Token);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            (await ClaudeAccountMonitor.RefusedConnectionsAsync(query, cts.Token)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task The_github_jira_and_linear_lookups_never_pick_a_claude_code_connection()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        string root = Path.Join(Path.GetTempPath(), "h9k-store-test");
        // Two claude-code connections, recorded before every other provider: an unfiltered pick would take one
        // of them, and an unfiltered count would see more than one Jira or Linear connection.
        await RegisterAsync(WorkItemProvider.ClaudeCode, Path.Join(root, ".claude-alt"), CredentialReference.ClaudeConfigDirectory);
        await RegisterAsync(WorkItemProvider.ClaudeCode, Path.Join(root, ".claude-other"), CredentialReference.ClaudeConfigDirectory);
        Guid github = await RegisterAsync(WorkItemProvider.GitHub, "someone", CredentialReference.GhCli);
        Guid jira = await RegisterAsync(WorkItemProvider.Jira, "someone@example.com", CredentialReference.EnvironmentVariable("JIRA_TOKEN"),
            new Uri("https://example.atlassian.net"));
        Guid linear = await RegisterAsync(WorkItemProvider.Linear, "someone", CredentialReference.EnvironmentVariable("LINEAR_TOKEN"),
            new Uri("https://linear.app/example"));

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            ((await WorkItemConnections.FindJiraConnectionAsync(query, cts.Token))?.Id).Should().Be(jira);
            ((await WorkItemConnections.FindLinearConnectionAsync(query, cts.Token))?.Id).Should().Be(linear);
        }

        // An owner recorded up front, so the bootstrap reads it rather than asking git config for a name.
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Guid ownerId = DomainId.New();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(ownerId, "someone", null, Now));
        await session.SaveChangesAsync(cts.Token);
        (await NodeBootstrap.EnsureAsync(session, cts.Token, machineNameOverride: "claude-code-lookup-test"))
            .ConnectionId.Should().Be(github);
    }
}
