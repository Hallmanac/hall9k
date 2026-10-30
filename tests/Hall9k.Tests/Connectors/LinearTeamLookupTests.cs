using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Confirming that a team with a key is visible through the connection, before a project is bound to
/// it. Every response is a fixture written from Linear's public schema (<c>Query.teams</c> with a
/// <c>TeamFilter</c> key filter, <c>Team.id</c>, <c>Team.key</c>, <c>Team.name</c>) and the documented
/// error envelope; none reaches a live workspace.
/// </summary>
public sealed class LinearTeamLookupTests : IDisposable
{
    private const string KeyFileName = "linear-team-test-key";
    private const string SecretKey = "lin_api_secret_value_for_team_tests";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");
    private static readonly CredentialReference KeyFile = CredentialReference.File(KeyFileName);

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public LinearTeamLookupTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), SecretKey);
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private sealed class RecordingRequester(int statusCode, string body, string? retryAfter = null)
    {
        public List<LinearRequest> Requests { get; } = [];

        public LinearRequester Requester => (request, _) =>
        {
            Requests.Add(request);
            return Task.FromResult(new LinearResponse(statusCode, body, retryAfter));
        };
    }

    private static LinearWorkItemProvider Provider(RecordingRequester requester) =>
        new(LinearAccount.Registered(Workspace, "Brian Hall", KeyFile), requester.Requester);

    private static string Teams(params (string Id, string Key, string Name)[] teams) =>
        JsonSerializer.Serialize(new
        {
            data = new { teams = new { nodes = teams.Select(team => new { id = team.Id, key = team.Key, name = team.Name }) } },
        });

    [Fact]
    public async Task A_visible_team_is_found_by_its_key_through_the_teams_query_with_a_key_filter()
    {
        RecordingRequester requester = new(200, Teams(("team-1", "ENG", "Engineering")));

        LinearTeam? team = await Provider(requester).FindTeamAsync(LinearTeamKey.Parse("eng"), Token);

        team.Should().Be(new LinearTeam("team-1", "ENG", "Engineering"));
        LinearRequest request = requester.Requests.Should().ContainSingle().Subject;
        using JsonDocument body = JsonDocument.Parse(request.JsonBody);
        body.RootElement.GetProperty("query").GetString().Should().Contain("teams(").And.Contain("key: { eq: $key }")
            .And.NotContain("team(id", "team(id:) is not documented to take a key");
        body.RootElement.GetProperty("variables").GetProperty("key").GetString().Should().Be("ENG");
        request.Authorization.Should().Be(SecretKey, "a personal key goes in the header as it is, with no scheme");
    }

    [Fact]
    public async Task A_team_that_is_not_visible_is_null_rather_than_an_error()
    {
        RecordingRequester requester = new(200, Teams());

        LinearTeam? team = await Provider(requester).FindTeamAsync(LinearTeamKey.Parse("NOPE"), Token);

        team.Should().BeNull("Linear answers the same way for a team that does not exist and one a limited key cannot see");
    }

    [Fact]
    public async Task An_answer_that_ignored_the_filter_cannot_bind_a_different_team()
    {
        RecordingRequester requester = new(200, Teams(("team-9", "OPS", "Operations")));

        LinearTeam? team = await Provider(requester).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        team.Should().BeNull();
    }

    [Fact]
    public async Task A_rejected_key_is_thrown_and_never_mistaken_for_an_absent_team()
    {
        RecordingRequester requester = new(401, """{"errors":[{"message":"Authentication required","extensions":{"code":"AUTHENTICATION_ERROR"}}]}""");

        Func<Task> find = () => Provider(requester).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        (await find.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("rejected the API key").And.Contain("h9k connection add linear").And.NotContain(SecretKey);
    }

    [Fact]
    public async Task Partial_data_beside_an_errors_array_is_a_failure_not_a_team()
    {
        RecordingRequester requester = new(
            200,
            """{"data":{"teams":{"nodes":[{"id":"t","key":"ENG","name":"Engineering"}]}},"errors":[{"message":"partial"}]}""");

        Func<Task> find = () => Provider(requester).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        await find.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_answer_that_is_not_a_list_of_teams_is_refused_rather_than_read_as_no_team()
    {
        RecordingRequester requester = new(200, """{"data":{"viewer":{"id":"u"}}}""");

        Func<Task> find = () => Provider(requester).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        (await find.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("not a list of teams");
    }
}
