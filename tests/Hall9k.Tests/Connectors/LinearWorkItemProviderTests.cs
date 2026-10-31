using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// What the Linear connector does with the answers Linear gives. Every response here is a fixture
/// written from Linear's public GraphQL schema (<c>Query.viewer</c>, <c>Query.organization</c>,
/// <c>Query.issue</c>, <c>Issue.previousIdentifiers</c>, <c>WorkflowState.type</c>) and the documented
/// error envelope; none reaches a live workspace, which the requester seam exists to guarantee.
/// </summary>
public sealed class LinearWorkItemProviderTests : IDisposable
{
    private const string KeyFileName = "linear-test-key";
    private const string SecretKey = "lin_api_secret_value_for_tests";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 30, 0, TimeSpan.Zero);
    private static readonly Uri Workspace = new("https://linear.app/hall9k");

    private static readonly CredentialReference KeyFile = CredentialReference.File(KeyFileName);

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public LinearWorkItemProviderTests()
    {
        // The key lives in a file under this test's own scoped home rather than in a process-wide
        // environment variable, which every test in the parallel unit tier would otherwise share.
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), SecretKey);
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    /// <summary>Answers every call with one fixture response, and keeps what it was asked.</summary>
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
        new(
            LinearAccount.Registered(Workspace, "Brian Hall", KeyFile),
            requester.Requester,
            new FixedClock(Now));

    private static string Issue(
        string identifier = "ENG-123", string stateType = "unstarted", string stateName = "Todo",
        string[]? previous = null, string? description = "Needs a limiter.", string? errors = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data"] = new
            {
                issue = new
                {
                    identifier,
                    title = "Add rate limiting",
                    description,
                    url = $"https://linear.app/hall9k/issue/{identifier}/add-rate-limiting",
                    previousIdentifiers = previous ?? [],
                    state = new { name = stateName, type = stateType },
                },
            },
            ["errors"] = errors is null ? null : JsonSerializer.Deserialize<JsonElement>(errors),
        }.Where(entry => entry.Value is not null).ToDictionary());

    private static LinearIssueIdentifier Eng(int number = 123) => new("ENG", number);

    [Theory]
    [InlineData("backlog", "Backlog")]
    [InlineData("unstarted", "Todo")]
    [InlineData("started", "In Progress")]
    public async Task Planned_and_started_work_reads_as_open_with_the_teams_own_word_kept(string type, string name)
    {
        RecordingRequester requester = new(200, Issue(stateType: type, stateName: name));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(), Token);

        item.Status.IsOpen.Should().BeTrue();
        item.Status.SourceLabel.Should().Be(name, "the observation is the team's word, not the platform's conclusion");
    }

    [Theory]
    [InlineData("completed", "Done")]
    [InlineData("canceled", "Canceled")]
    [InlineData("duplicate", "Duplicate")]
    public async Task Finished_work_reads_as_closed(string type, string name)
    {
        RecordingRequester requester = new(200, Issue(stateType: type, stateName: name));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(), Token);

        item.Status.IsOpen.Should().BeFalse();
        item.Status.IsTriage.Should().BeFalse();
        item.Status.Value.Should().Be(WorkItemStatus.Closed.Value);
    }

    [Fact]
    public async Task Triage_is_told_apart_from_every_other_state_that_is_not_open()
    {
        RecordingRequester requester = new(200, Issue(stateType: "triage", stateName: "Triage"));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(), Token);

        item.Status.IsTriage.Should().BeTrue();
        item.Status.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_state_type_nobody_has_a_rule_for_keeps_its_own_word_and_maps_to_nothing()
    {
        RecordingRequester requester = new(200, Issue(stateType: "paused", stateName: "Open"));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(), Token);

        item.Status.IsOpen.Should().BeFalse("a state named Open is not the source saying open");
        item.Status.ToString().Should().Contain("paused");
    }

    [Fact]
    public async Task The_issue_is_recorded_under_the_identifier_linear_answered_not_the_text_typed()
    {
        RecordingRequester requester = new(200, Issue(identifier: "PLAT-9"));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(5), Token);

        item.Reference.ToString().Should().Be("linear:PLAT-9");
        item.Title.Should().Be("Add rate limiting");
        item.Body.Should().Be("Needs a limiter.");
        item.ObservedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_description_of_null_is_no_description()
    {
        RecordingRequester requester = new(200, Issue(description: null));

        ImportedWorkItem item = await Provider(requester).ReadAsync(Eng(), Token);

        item.Body.Should().BeNull();
    }

    [Fact]
    public async Task The_request_is_a_graphql_post_to_the_one_endpoint_authenticated_with_the_bare_key()
    {
        RecordingRequester requester = new(200, Issue());

        await Provider(requester).ReadAsync(Eng(), Token);

        LinearRequest request = requester.Requests.Should().ContainSingle().Subject;
        request.Url.Should().Be(new Uri("https://api.linear.app/graphql"));
        request.Authorization.Should().Be(SecretKey, "a personal API key goes in the header with no Bearer");
        using JsonDocument body = JsonDocument.Parse(request.JsonBody);
        body.RootElement.GetProperty("variables").GetProperty("id").GetString().Should().Be("ENG-123");
        body.RootElement.GetProperty("query").GetString().Should().Contain("issue(id: $id)");
    }

    [Fact]
    public async Task Previous_identifiers_ride_along_for_a_relink()
    {
        RecordingRequester requester = new(200, Issue(identifier: "PLAT-9", previous: ["ENG-123", "OPS-2"]));

        LinearIssueRead read = await Provider(requester).ReadIssueAsync(new LinearIssueIdentifier("PLAT", 9), cancellationToken: Token);

        read.PreviousIdentifiers.Should().BeEquivalentTo("ENG-123", "OPS-2");
        read.Item.FormerReferences.Should().BeEquivalentTo(
        [
            new ExternalReference(WorkItemProvider.Linear, "ENG-123"),
            new ExternalReference(WorkItemProvider.Linear, "OPS-2"),
        ]);
    }

    // ── the viewer ──

    [Fact]
    public async Task The_viewer_read_reports_who_the_key_is_and_which_workspace_it_reads()
    {
        RecordingRequester requester = new(200, """
        {"data":{"viewer":{"id":"2f1c0d1e-0000-4000-8000-000000000001","name":"Brian Hall"},
                 "organization":{"urlKey":"hall9k"}}}
        """);

        LinearViewer viewer = await Provider(requester).VerifyAccessAsync(Token);

        viewer.Should().Be(new LinearViewer("2f1c0d1e-0000-4000-8000-000000000001", "Brian Hall", "hall9k"));
        LinearAccount.WorkspaceFor(viewer.WorkspaceKey).Should().Be(Workspace);
    }

    [Fact]
    public async Task A_key_in_hand_is_proven_before_anything_stores_it()
    {
        RecordingRequester requester = new(200, """
        {"data":{"viewer":{"id":"u1","name":"Brian Hall"},"organization":{"urlKey":"hall9k"}}}
        """);
        LinearWorkItemProvider provider = new(LinearAccount.WithKeyInHand("a-fresh-key"), requester.Requester);

        await provider.VerifyAccessAsync(Token);

        requester.Requests.Single().Authorization.Should().Be("a-fresh-key");
    }

    [Fact]
    public async Task A_two_hundred_that_is_not_a_viewer_proves_nothing()
    {
        RecordingRequester requester = new(200, """{"data":{"viewer":null,"organization":null}}""");

        Func<Task> verify = () => Provider(requester).VerifyAccessAsync(Token);

        (await verify.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("not a viewer and a workspace");
    }

    [Theory]
    [InlineData("hall9k", true)]
    [InlineData("my-co_1.x", true)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void The_workspace_key_must_read_as_the_slug_in_a_linear_address(string key, bool accepted)
    {
        Action build = () => LinearAccount.WorkspaceFor(key);

        if (accepted)
        {
            build.Should().NotThrow();
        }
        else
        {
            build.Should().Throw<DomainValidationException>();
        }
    }

    // ── failures ──

    [Fact]
    public async Task A_graphql_errors_array_is_a_failure_even_on_http_200_with_partial_data()
    {
        RecordingRequester requester = new(200, Issue(
            errors: """[{"message":"Something partial went wrong","extensions":{"type":"internal error"}}]"""));

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.Read.Should().BeNull("data beside an errors array is not trusted");
        attempt.Failure!.Message.Should().Contain("Something partial went wrong");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(200)]
    public async Task Errors_are_a_failure_whatever_the_http_status(int status)
    {
        RecordingRequester requester = new(status, """{"errors":[{"message":"Boom happened"}]}""");

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.Read.Should().BeNull();
        attempt.Failure!.Message.Should().Contain("Boom happened");
    }

    [Theory]
    [InlineData(429, "{\"errors\":[{\"message\":\"Rate limit exceeded\",\"extensions\":{\"code\":\"RATELIMITED\"}}]}")]
    [InlineData(200, "{\"errors\":[{\"message\":\"Rate limit exceeded\",\"extensions\":{\"code\":\"RATELIMITED\"}}]}")]
    [InlineData(429, "{}")]
    public async Task A_rate_limit_is_an_outage_that_reports_retry_after(int status, string body)
    {
        RecordingRequester requester = new(status, body, retryAfter: "47");

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Outage);
        attempt.Failure!.Message.Should().Contain("rate-limiting").And.Contain("Retry-After is 47");
    }

    [Theory]
    [InlineData(503, "{\"errors\":[{\"message\":\"Upstream not found\"}]}")]
    [InlineData(500, "{\"errors\":[{\"message\":\"Boom\",\"extensions\":{\"code\":\"AUTHENTICATION_ERROR\"}}]}")]
    public async Task A_server_error_is_an_outage_whatever_its_errors_array_says(int status, string body)
    {
        RecordingRequester requester = new(status, body);

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Outage);
        attempt.Failure!.Message.Should().Contain($"HTTP {status}");
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(200, "{\"errors\":[{\"message\":\"Authentication required\",\"extensions\":{\"type\":\"authentication error\",\"code\":\"AUTHENTICATION_ERROR\"}}]}")]
    [InlineData(400, "{\"errors\":[{\"message\":\"nope\",\"extensions\":{\"code\":\"AUTHENTICATION_ERROR\"}}]}")]
    public async Task A_rejected_key_is_told_apart_and_carries_the_remedy(int status, string body)
    {
        RecordingRequester requester = new(status, body);

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Credentials);
        attempt.Failure!.Message.Should().Contain("rejected the API key").And.Contain("h9k connection add linear");
    }

    [Fact]
    public async Task An_unreadable_item_is_told_apart_from_a_bad_key_and_an_outage()
    {
        RecordingRequester requester = new(200, """
        {"errors":[{"message":"Entity not found: Issue - Could not find referenced Issue.","extensions":{"type":"invalid input"}}],"data":null}
        """);

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Item);
        attempt.Failure.Should().BeOfType<DomainNotFoundException>();
    }

    [Fact]
    public async Task A_server_error_is_an_outage()
    {
        RecordingRequester requester = new(503, "<html>down</html>");

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Outage);
    }

    [Fact]
    public async Task An_error_nothing_recognizes_quotes_linears_own_message_on_one_line()
    {
        RecordingRequester requester = new(200, """
        {"errors":[{"message":"First line\nsecond line [odd]","extensions":{"code":"SOMETHING_NEW"}}]}
        """);

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Item);
        attempt.Failure!.Message.Should().Contain("Linear reported: First line second line [odd]")
            .And.NotContain("\n");
    }

    [Fact]
    public async Task A_null_issue_with_no_error_is_not_found_too()
    {
        RecordingRequester requester = new(200, """{"data":{"issue":null}}""");

        Func<Task> read = () => Provider(requester).ReadAsync(Eng(), Token);

        (await read.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("Could not find ENG-123").And.Contain("limited to other teams");
    }

    [Fact]
    public async Task Not_found_for_a_linked_issue_says_it_may_have_moved_and_names_the_relink()
    {
        RecordingRequester requester = new(200, """{"data":{"issue":null}}""");

        Func<Task> read = () => Provider(requester).ReadIssueAsync(Eng(), "28b19893", Token);

        (await read.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("may have moved to another team")
            .And.Contain("new identifier")
            .And.Contain("h9k task link-linear 28b19893 <new identifier>");
    }

    [Theory]
    [InlineData(200, """{"errors":[{"message":"Entity not found: Issue - Could not find referenced Issue.","extensions":{"type":"invalid input"}}],"data":null}""")]
    [InlineData(404, """{"errors":[{"message":"Entity not found: Issue"}]}""")]
    [InlineData(404, "not found")]
    public async Task Not_found_reported_as_an_error_for_a_linked_issue_also_names_the_relink(int status, string body)
    {
        RecordingRequester requester = new(status, body);

        Func<Task> read = () => Provider(requester).ReadIssueAsync(Eng(), "28b19893", Token);

        (await read.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("may have moved to another team")
            .And.Contain("h9k task link-linear 28b19893 <new identifier>");
    }

    [Fact]
    public async Task A_key_file_that_is_gone_names_the_linear_command_to_register_again()
    {
        File.Delete(CredentialVault.FileFor(KeyFileName));
        RecordingRequester requester = new(200, "{}");

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.FailureKind.Should().Be(TrackerReadFailure.Credentials);
        attempt.Failure!.Message.Should().Contain("h9k connection add linear").And.NotContain("add jira");
        requester.Requests.Should().BeEmpty("no request goes out without a key");
    }

    [Fact]
    public async Task A_body_that_is_not_graphql_is_refused_rather_than_read_as_an_empty_answer()
    {
        RecordingRequester requester = new(200, "<html>sign in</html>");

        Func<Task> read = () => Provider(requester).ReadAsync(Eng(), Token);

        (await read.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("not a GraphQL response");
    }

    [Fact]
    public async Task A_timeout_is_an_outage_and_a_key_the_vault_cannot_produce_is_a_credential_failure()
    {
        LinearWorkItemProvider timingOut = new(
            LinearAccount.Registered(Workspace, "Brian", KeyFile),
            (_, _) => throw new TaskCanceledException());
        LinearWorkItemProvider unset = new(
            LinearAccount.Registered(Workspace, "Brian", CredentialReference.EnvironmentVariable("HALL9K_TEST_LINEAR_UNSET")),
            FakeLinearRequester.NeverInvoked());

        (await timingOut.TryReadIssueAsync(Eng(), null, Token)).FailureKind.Should().Be(TrackerReadFailure.Outage);
        (await unset.TryReadIssueAsync(Eng(), null, Token)).FailureKind.Should().Be(TrackerReadFailure.Credentials);
    }

    [Fact]
    public async Task No_failure_message_ever_contains_the_key()
    {
        RecordingRequester requester = new(401, "{}");

        LinearIssueAttempt attempt = await Provider(requester).TryReadIssueAsync(Eng(), null, Token);

        attempt.Failure!.Message.Should().NotContain(SecretKey);
    }

    // ── placing a reference ──

    [Fact]
    public void A_linear_reference_renders_under_the_registered_workspace()
    {
        LinearWorkItemProvider provider = Provider(new RecordingRequester(200, "{}"));

        provider.WebUrl(ExternalReference.Parse("linear:ENG-123")).Should()
            .Be(new Uri("https://linear.app/hall9k/issue/ENG-123"));
    }

    [Theory]
    [InlineData("linear:not an identifier")]
    [InlineData("jira:ENG-123")]
    public void A_reference_that_is_not_a_linear_issue_is_not_dressed_up_as_one(string reference)
    {
        LinearWorkItemProvider provider = Provider(new RecordingRequester(200, "{}"));

        provider.WebUrl(ExternalReference.Parse(reference)).Should().BeNull();
    }

    // ── the importer's gate over this provider ──

    private static WorkItemImporter Importer(RecordingRequester requester) => new(Provider(requester));

    private static Task<ImportedWorkItem> ImportAsync(WorkItemImporter importer, CancellationToken token) =>
        importer.ImportAsync(new WorkItemImportRequest(WorkItemProvider.Linear, "ENG-123", Path.GetTempPath()), token);

    [Theory]
    [InlineData("backlog")]
    [InlineData("unstarted")]
    [InlineData("started")]
    public async Task Adoption_takes_the_open_state_types(string type)
    {
        ImportedWorkItem item = await ImportAsync(Importer(new RecordingRequester(200, Issue(stateType: type))), Token);

        item.Reference.ToString().Should().Be("linear:ENG-123");
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("canceled")]
    [InlineData("duplicate")]
    public async Task Adoption_refuses_finished_work(string type)
    {
        Func<Task> import = () => ImportAsync(Importer(new RecordingRequester(200, Issue(stateType: type))), Token);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("was not open when Hall9k read it");
    }

    [Fact]
    public async Task Adoption_refuses_triage_with_a_pointer_to_the_idea_door()
    {
        Func<Task> import = () => ImportAsync(
            Importer(new RecordingRequester(200, Issue(stateType: "triage", stateName: "Triage"))), Token);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("in triage").And.Contain("h9k idea add \"ENG-123: ")
            .And.NotContain("Reopen it");
    }

    [Fact]
    public async Task Adoption_refuses_a_type_hall9k_has_no_rule_for_and_quotes_it()
    {
        Func<Task> import = () => ImportAsync(
            Importer(new RecordingRequester(200, Issue(stateType: "paused"))), Token);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("paused");
    }

    [Fact]
    public async Task Adoption_refuses_a_url_from_another_workspace_before_calling_linear()
    {
        WorkItemImporter importer = new(new LinearWorkItemProvider(
            LinearAccount.Registered(Workspace, "Brian", KeyFile),
            FakeLinearRequester.NeverInvoked()));

        Func<Task> import = () => importer.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.Linear, "https://linear.app/other-co/issue/ENG-1", Path.GetTempPath()),
            Token);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("other-co");
    }
}
