using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The Linear branch of the linked-item scan an adopted or reviewed pull request goes through, with the
/// store behind it that resolves the connection. Every Linear response is a recorded fixture written
/// from Linear's public schema; no test reaches a live workspace, and none touches GitHub: the
/// pull request is a value handed to the scan.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LinkedLinearImportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string KeyFileName = "linked-linear-import-test-key";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(2));

    private CancellationToken Token => _cancellation.Token;

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private sealed class RecordedLinear(string teamsBody, int teamsStatus = 200)
    {
        public List<string> Queries { get; } = [];

        public LinearRequester Requester => (request, _) =>
        {
            using JsonDocument body = JsonDocument.Parse(request.JsonBody);
            string query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            Queries.Add(query);
            return Task.FromResult(query.Contains("HallTeamKeys", StringComparison.Ordinal)
                ? new LinearResponse(teamsStatus, teamsBody)
                : new LinearResponse(200, IssueBody(body.RootElement.GetProperty("variables").GetProperty("id").GetString()!)));
        };
    }

    private static string IssueBody(string identifier) => JsonSerializer.Serialize(new
    {
        data = new
        {
            issue = new
            {
                identifier,
                title = $"The login page of {identifier}",
                description = "Sign-in is slow.",
                url = $"https://linear.app/hall9k/issue/{identifier}/the-login-page",
                previousIdentifiers = Array.Empty<string>(),
                state = new { name = "Todo", type = "unstarted" },
            },
        },
    });

    private static string Teams(params string[] keys) => JsonSerializer.Serialize(new
    {
        data = new
        {
            teams = new
            {
                nodes = keys.Select(key => new { key }),
                pageInfo = new { hasNextPage = false, endCursor = (string?)null },
            },
        },
    });

    private async Task RegisterLinearAsync()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        await File.WriteAllTextAsync(CredentialVault.FileFor(KeyFileName), "lin_api_scan_test_key", Token);
        await using IDocumentSession clear = postgres.Store.LightweightSession();
        clear.DeleteWhere<ConnectionDetails>(connection => true);
        await clear.SaveChangesAsync(Token);

        Guid id = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(id, new ConnectionRegistered(
            id, DomainId.New(), WorkItemProvider.Linear, "Brian Hall", CredentialReference.File(KeyFileName), Now, Workspace));
        await session.SaveChangesAsync(Token);
    }

    private async Task ClearConnectionsAsync()
    {
        await using IDocumentSession clear = postgres.Store.LightweightSession();
        clear.DeleteWhere<ConnectionDetails>(connection => true);
        await clear.SaveChangesAsync(Token);
    }

    private static ProjectDetails Project(string linearTeam) => new()
    {
        Id = DomainId.New(),
        Name = "hall9k",
        RepositoryPath = Path.GetTempPath(),
        LinearTeam = LinearTeamKey.Parse(linearTeam),
    };

    private static ImportedWorkItem PullRequest(string title, string body) => new(
        new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#7"), title, body, WorkItemStatus.Open, null, Now);

    private async Task<string?> ScanAsync(ProjectDetails project, ImportedWorkItem pullRequest, LinearRequester requester)
    {
        await using IQuerySession session = postgres.Store.QuerySession();
        return await LinkedWorkItemImport.TryImportContextAsync(
            session, project, pullRequest, Token, processRunner: null, linearRequester: requester);
    }

    [Fact]
    public async Task An_issue_from_a_team_other_than_the_default_is_imported_and_a_look_alike_is_not()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new(Teams("ENG", "DES"));

        string? context = await ScanAsync(
            Project("ENG"), PullRequest("Speed up SHA-256 checks", "Closes DES-5 and also hashes UTF-8."), linear.Requester);

        context.Should().Contain("Imported from linear:DES-5.").And.Contain("The login page of DES-5");
        linear.Queries.Count(query => query.Contains("HallTeamKeys", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task A_Linear_identifier_wins_over_a_GitHub_closing_reference_in_the_same_text()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new(Teams("ENG"));

        string? context = await ScanAsync(
            Project("ENG"), PullRequest("Fixes #42", "Fixes #42, tracked as https://linear.app/hall9k/issue/ENG-9/slug"),
            linear.Requester);

        context.Should().Contain("Imported from linear:ENG-9.");
    }

    [Fact]
    public async Task Text_with_no_key_shaped_token_makes_no_Linear_call()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new(Teams("ENG"));

        await ScanAsync(Project("ENG"), PullRequest("Tidy the readme", "Nothing to see."), linear.Requester);

        linear.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_that_only_looks_like_a_key_imports_nothing()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new(Teams("ENG"));

        string? context = await ScanAsync(
            Project("ENG"), PullRequest("Use SHA-256", "Also CVE-2024-1."), linear.Requester);

        context.Should().BeNull();
        linear.Queries.Should().ContainSingle("only the team keys were asked for");
    }

    [Fact]
    public async Task A_project_that_is_not_Linear_tracked_makes_no_Linear_call_and_behaves_as_it_did()
    {
        await RegisterLinearAsync();

        string? context = await ScanAsync(
            Project(string.Empty), PullRequest("Fix ENG-9", "Closes DES-5."), FakeLinearRequesterNeverInvoked());

        context.Should().BeNull("no Jira board is bound and no GitHub closing reference is in the text");
    }

    [Fact]
    public async Task With_no_Linear_connection_the_scan_adds_no_context_and_does_not_fail()
    {
        await ClearConnectionsAsync();

        string? context = await ScanAsync(
            Project("ENG"), PullRequest("Fix ENG-9", "Closes DES-5."), FakeLinearRequesterNeverInvoked());

        context.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_team_read_adds_no_context_even_where_a_GitHub_reference_is_present()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new("upstream down", teamsStatus: 500);

        string? context = await ScanAsync(
            Project("ENG"), PullRequest("Fixes #42", "Fixes #42 and Closes ENG-9."), linear.Requester);

        context.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_issue_read_adds_no_context()
    {
        await RegisterLinearAsync();
        LinearRequester requester = (request, _) => Task.FromResult(
            request.JsonBody.Contains("HallTeamKeys", StringComparison.Ordinal)
                ? new LinearResponse(200, Teams("ENG"))
                : new LinearResponse(500, "upstream down"));

        string? context = await ScanAsync(Project("ENG"), PullRequest("Fix", "Closes ENG-9."), requester);

        context.Should().BeNull();
    }

    [Fact]
    public async Task The_opener_reads_the_same_keys_through_the_same_helper_and_reads_no_connection_as_a_failure()
    {
        await RegisterLinearAsync();
        RecordedLinear linear = new(Teams("ENG", "DES"));
        PullRequestOpener opener = new(
            postgres.Store, NullLogger<PullRequestOpener>.Instance, linearRequester: linear.Requester);

        IReadOnlySet<string>? keys = await opener.ReadLinearTeamKeysAsync(Token);

        keys.Should().BeEquivalentTo("ENG", "DES");

        await ClearConnectionsAsync();
        (await opener.ReadLinearTeamKeysAsync(Token)).Should().BeNull();
    }

    private static LinearRequester FakeLinearRequesterNeverInvoked() => Fakes.FakeLinearRequester.NeverInvoked();
}
