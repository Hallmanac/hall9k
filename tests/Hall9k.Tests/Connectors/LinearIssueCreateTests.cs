using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Creating an issue in Linear: the one <c>issueCreate</c> request, and how every way an answer can
/// come back is told apart, because a definite refusal and an unknown outcome call for different
/// remedies (a by-hand create, or a search before any create, since the issue may exist). Responses
/// are fixtures written from Linear's public schema (<c>IssueCreateInput</c>, <c>IssuePayload</c>,
/// <c>Team.defaultIssueState</c>) and its documented error envelope; none reaches a live workspace.
/// </summary>
public sealed class LinearIssueCreateTests : IDisposable
{
    private const string KeyFileName = "linear-create-test-key";
    private const string SecretKey = "lin_api_secret_value_for_create_tests";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");
    private static readonly CredentialReference KeyFile = CredentialReference.File(KeyFileName);

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public LinearIssueCreateTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), SecretKey);
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private sealed class Recorded(Func<LinearRequest, Task<LinearResponse>> answer)
    {
        public List<LinearRequest> Requests { get; } = [];

        public LinearRequester Requester => (request, _) =>
        {
            Requests.Add(request);
            return answer(request);
        };

        public static Recorded Answering(int status, string body) =>
            new(_ => Task.FromResult(new LinearResponse(status, body)));
    }

    private static LinearWorkItemProvider Provider(Recorded recorded) =>
        new(LinearAccount.Registered(Workspace, "Brian Hall", KeyFile), recorded.Requester);

    private static LinearIssueCreateRequest Sample(string? stateId = "state-backlog") =>
        new("team-1", stateId, "Publishing creates the issue", "Agent context.\n\n## Acceptance criteria\n\n- [ ] It is created");

    private const string Created = """{"data":{"issueCreate":{"success":true,"issue":{"identifier":"ENG-42"}}}}""";

    [Fact]
    public async Task The_request_is_one_issueCreate_with_the_team_the_state_the_title_and_the_description_and_nothing_else()
    {
        Recorded recorded = Recorded.Answering(200, Created);

        await Provider(recorded).CreateIssueAsync(Sample(), Token);

        LinearRequest request = recorded.Requests.Should().ContainSingle().Subject;
        request.Authorization.Should().Be(SecretKey);
        request.JsonBody.Should().Be(
            "{\"query\":\"mutation HallIssueCreate($input: IssueCreateInput!) "
            + "{ issueCreate(input: $input) { success issue { identifier } } }\","
            + "\"variables\":{\"input\":{\"teamId\":\"team-1\",\"stateId\":\"state-backlog\","
            + "\"title\":\"Publishing creates the issue\","
            + "\"description\":\"Agent context.\\n\\n## Acceptance criteria\\n\\n- [ ] It is created\"}}}");
        using JsonDocument body = JsonDocument.Parse(request.JsonBody);
        JsonElement input = body.RootElement.GetProperty("variables").GetProperty("input");
        input.EnumerateObject().Select(property => property.Name)
            .Should().Equal("teamId", "stateId", "title", "description")
            .And.NotContain(["assigneeId", "labelIds", "projectId", "cycleId", "id"]);
    }

    [Fact]
    public async Task The_state_is_omitted_when_Linear_reports_no_default_state_for_the_team()
    {
        Recorded recorded = Recorded.Answering(200, Created);

        await Provider(recorded).CreateIssueAsync(Sample(stateId: null), Token);

        using JsonDocument body = JsonDocument.Parse(recorded.Requests.Single().JsonBody);
        body.RootElement.GetProperty("variables").GetProperty("input").TryGetProperty("stateId", out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_issue_Linear_created_is_returned_by_its_identifier()
    {
        LinearIssueCreation creation = await Provider(Recorded.Answering(200, Created)).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Created);
        creation.Identifier!.Value.Should().Be("ENG-42");
        creation.Failure.Should().BeNull();
    }

    [Theory]
    [InlineData(200, """{"errors":[{"message":"teamId must be a UUID","extensions":{"type":"invalid input"}}],"data":null}""")]
    [InlineData(200, """{"data":{"issueCreate":{"success":false,"issue":null}}}""")]
    [InlineData(400, """{"errors":[{"message":"Argument Validation Error"}]}""")]
    [InlineData(401, """{"errors":[{"message":"Authentication required","extensions":{"type":"authentication error"}}]}""")]
    [InlineData(403, "Forbidden")]
    [InlineData(429, """{"errors":[{"message":"Rate limit exceeded","extensions":{"code":"RATELIMITED"}}]}""")]
    public async Task A_definite_refusal_is_one_Linear_answered_without_creating_anything(int status, string body)
    {
        Recorded recorded = Recorded.Answering(status, body);

        LinearIssueCreation creation = await Provider(recorded).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Refused);
        creation.Identifier.Should().BeNull();
        creation.Failure.Should().NotBeNull();
        recorded.Requests.Should().ContainSingle("a create is sent once and never retried");
    }

    [Theory]
    [InlineData(500, "Internal Server Error")]
    [InlineData(502, "<html>Bad gateway</html>")]
    [InlineData(503, """{"errors":[{"message":"Service unavailable"}]}""")]
    [InlineData(200, "<html>a captive portal</html>")]
    [InlineData(200, """{"data":{"issueCreate":{"success":true,"issue":null}}}""")]
    [InlineData(200, """{"data":{"issueCreate":{"success":true,"issue":{"identifier":"not an identifier"}}}}""")]
    [InlineData(200, """{"data":{}}""")]
    public async Task An_outcome_that_cannot_be_read_or_that_failed_on_Linears_side_is_unknown_because_the_issue_may_exist(
        int status, string body)
    {
        Recorded recorded = Recorded.Answering(status, body);

        LinearIssueCreation creation = await Provider(recorded).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Unknown);
        creation.Identifier.Should().BeNull();
        recorded.Requests.Should().ContainSingle("a create is sent once and never retried");
    }

    [Fact]
    public async Task A_timeout_is_unknown_because_Linear_may_have_acted_on_the_request_before_it_was_lost()
    {
        Recorded recorded = new(_ => Task.FromException<LinearResponse>(new TaskCanceledException("timed out")));

        LinearIssueCreation creation = await Provider(recorded).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Unknown);
        recorded.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_lost_connection_is_unknown()
    {
        Recorded recorded = new(_ => Task.FromException<LinearResponse>(new HttpRequestException("connection reset")));

        LinearIssueCreation creation = await Provider(recorded).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Unknown);
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_from_the_vault_is_a_refusal_because_nothing_was_sent()
    {
        File.Delete(CredentialVault.FileFor(KeyFileName));
        Recorded recorded = Recorded.Answering(200, Created);

        LinearIssueCreation creation = await Provider(recorded).CreateIssueAsync(Sample(), Token);

        creation.Outcome.Should().Be(LinearCreateOutcome.Refused);
        recorded.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_team_lookup_carries_the_id_of_its_default_issue_state_and_asks_for_it()
    {
        Recorded recorded = Recorded.Answering(
            200,
            """{"data":{"teams":{"nodes":[{"id":"team-1","key":"ENG","name":"Engineering","defaultIssueState":{"id":"state-backlog"}}]}}}""");

        LinearTeam? team = await Provider(recorded).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        team.Should().Be(new LinearTeam("team-1", "ENG", "Engineering", "state-backlog"));
        recorded.Requests.Single().JsonBody.Should().Contain("defaultIssueState { id }");
    }

    [Theory]
    [InlineData("""{"id":"team-1","key":"ENG","name":"Engineering","defaultIssueState":null}""")]
    [InlineData("""{"id":"team-1","key":"ENG","name":"Engineering"}""")]
    public async Task A_team_with_no_reported_default_state_has_none(string node)
    {
        Recorded recorded = Recorded.Answering(200, "{\"data\":{\"teams\":{\"nodes\":[" + node + "]}}}");

        LinearTeam? team = await Provider(recorded).FindTeamAsync(LinearTeamKey.Parse("ENG"), Token);

        team!.DefaultIssueStateId.Should().BeNull();
    }
}
