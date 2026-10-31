using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The one read of which team keys the install's Linear connection can see: every page of the
/// <c>teams</c> connection, one rule for failure, and a deadline. Every response is a fixture written
/// from Linear's public schema (<c>TeamConnection</c>, <c>PageInfo</c>) and the documented error
/// envelope; none reaches a live workspace.
/// </summary>
public sealed class LinearTeamKeysTests : IDisposable
{
    private const string KeyFileName = "linear-team-keys-test-key";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public LinearTeamKeysTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), "lin_api_secret_value_for_team_key_tests");
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private static LinearWorkItemProvider Provider(LinearRequester requester) =>
        new(LinearAccount.Registered(Workspace, "Brian Hall", CredentialReference.File(KeyFileName)), requester);

    private static string Page(bool hasNext, string? endCursor, params string[] keys) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                teams = new
                {
                    nodes = keys.Select(key => new { key }),
                    pageInfo = new { hasNextPage = hasNext, endCursor },
                },
            },
        });

    /// <summary>Answers by the cursor the request carries, as a connection does, and records the requests.</summary>
    private sealed class PagedTeams(IReadOnlyDictionary<string, LinearResponse> pages)
    {
        public List<string?> Cursors { get; } = [];

        public LinearRequester Requester => (request, _) =>
        {
            using JsonDocument body = JsonDocument.Parse(request.JsonBody);
            string? cursor = body.RootElement.GetProperty("variables").GetProperty("after").GetString();
            Cursors.Add(cursor);
            return Task.FromResult(pages[cursor ?? string.Empty]);
        };
    }

    private static LinearResponse Ok(string body) => new(200, body);

    [Fact]
    public async Task Every_page_of_teams_is_read_once_and_the_keys_are_upper_cased()
    {
        PagedTeams teams = new(new Dictionary<string, LinearResponse>
        {
            [string.Empty] = Ok(Page(true, "c1", "ENG", "des")),
            ["c1"] = Ok(Page(true, "c2", "OPS")),
            ["c2"] = Ok(Page(false, null, "PLAT")),
        });

        IReadOnlySet<string> keys = await LinearTeamKeys.TryReadAsync(Provider(teams.Requester), cancellationToken: Token)
            ?? throw new InvalidOperationException("the read was expected to succeed");

        keys.Should().BeEquivalentTo("ENG", "DES", "OPS", "PLAT");
        keys.Contains("des").Should().BeTrue("keys compare without regard to case");
        teams.Cursors.Should().Equal(null, "c1", "c2");
    }

    [Fact]
    public async Task A_page_that_fails_makes_the_whole_read_fail_and_a_partial_list_is_never_used()
    {
        PagedTeams teams = new(new Dictionary<string, LinearResponse>
        {
            [string.Empty] = Ok(Page(true, "c1", "ENG")),
            ["c1"] = new LinearResponse(500, "upstream down"),
        });

        (await LinearTeamKeys.TryReadAsync(Provider(teams.Requester), cancellationToken: Token)).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"Authentication required","extensions":{"code":"AUTHENTICATION_ERROR"}}]}""", 401)]
    [InlineData("""{"data":{"teams":{"nodes":[{"key":"ENG"}],"pageInfo":{"hasNextPage":false}}},"errors":[{"message":"partial"}]}""", 200)]
    [InlineData("""{"data":{"teams":{"nodes":[{"key":"ENG"}]}}}""", 200)]
    [InlineData("""{"data":{"teams":{"nodes":[{"id":"no-key"}],"pageInfo":{"hasNextPage":false}}}}""", 200)]
    [InlineData("""{"data":{"teams":{"nodes":[{"key":"ENG"}],"pageInfo":{"hasNextPage":true}}}}""", 200)]
    [InlineData("<html>a captive portal</html>", 200)]
    public async Task An_answer_that_is_not_a_complete_page_of_teams_is_a_failed_read(string body, int status)
    {
        LinearRequester requester = (_, _) => Task.FromResult(new LinearResponse(status, body));

        (await LinearTeamKeys.TryReadAsync(Provider(requester), cancellationToken: Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_list_that_never_ends_is_a_failed_read_rather_than_a_loop()
    {
        int calls = 0;
        LinearRequester requester = (_, _) => Task.FromResult(Ok(Page(true, $"cursor-{calls++}", "ENG")));

        (await LinearTeamKeys.TryReadAsync(Provider(requester), cancellationToken: Token)).Should().BeNull();
        calls.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public async Task A_cursor_that_does_not_advance_is_a_failed_read()
    {
        LinearRequester requester = (_, _) => Task.FromResult(Ok(Page(true, "same", "ENG")));

        (await LinearTeamKeys.TryReadAsync(Provider(requester), cancellationToken: Token)).Should().BeNull();
    }

    [Fact]
    public async Task No_connection_is_a_failed_read_and_asks_Linear_nothing()
    {
        (await LinearTeamKeys.TryReadAsync((LinearWorkItemProvider?)null, cancellationToken: Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_read_that_runs_past_the_deadline_is_a_failed_read()
    {
        LinearRequester requester = async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return Ok(Page(false, null, "ENG"));
        };

        IReadOnlySet<string>? keys = await LinearTeamKeys.TryReadAsync(
            Provider(requester), deadline: TimeSpan.FromMilliseconds(50), cancellationToken: Token);

        keys.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_of_the_callers_own_token_propagates_instead_of_reading_as_a_failure()
    {
        using CancellationTokenSource caller = new();
        LinearRequester requester = async (_, cancellation) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Ok(Page(false, null, "ENG"));
        };

        Func<Task> read = () => LinearTeamKeys.TryReadAsync(Provider(requester), cancellationToken: caller.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_key_the_vault_cannot_produce_is_a_failed_read()
    {
        LinearWorkItemProvider provider = new(
            LinearAccount.Registered(Workspace, "Brian Hall", CredentialReference.File("no-such-key-file")),
            FakeNeverCalled);

        (await LinearTeamKeys.TryReadAsync(provider, cancellationToken: Token)).Should().BeNull();
    }

    private static Task<LinearResponse> FakeNeverCalled(LinearRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the key could not be read, so Linear was never asked");

    [Fact]
    public async Task The_request_pages_in_the_largest_size_Linear_allows_and_carries_the_key_as_it_is()
    {
        PagedTeams teams = new(new Dictionary<string, LinearResponse> { [string.Empty] = Ok(Page(false, null, "ENG")) });
        List<LinearRequest> seen = [];
        LinearRequester recording = (request, cancellation) =>
        {
            seen.Add(request);
            return teams.Requester(request, cancellation);
        };

        await LinearTeamKeys.TryReadAsync(Provider(recording), cancellationToken: Token);

        LinearRequest request = seen.Should().ContainSingle().Subject;
        using JsonDocument body = JsonDocument.Parse(request.JsonBody);
        body.RootElement.GetProperty("query").GetString().Should().Contain("teams(first: 250").And.Contain("pageInfo");
        request.Authorization.Should().Be("lin_api_secret_value_for_team_key_tests");
    }
}
