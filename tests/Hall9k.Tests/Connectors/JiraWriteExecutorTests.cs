using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// What <see cref="JiraWriteExecutor"/> does with Jira's own structured HTTP answers — the
/// executor's transport moved off the Atlassian CLI (twg) onto this REST client (Decisions Log
/// #114), so what used to be regex-parsed out of a text envelope is now read straight off JSON:
/// the status-code classes the old envelope parsing guarded (success with a key, a 4xx refusal, an
/// auth failure, and a timeout after the request was sent) plus the write-safety logic that
/// survives the transport swap unchanged — the physical dedup gate, the verified read-back, and
/// the cancellation-grace-adjacent auth-vs-other classification a comment's own read-back needs.
/// </summary>
public sealed class JiraWriteExecutorTests
{
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static JiraWriteExecutor Executor(RecordingJiraRequester requester, TimeProvider? clock = null) =>
        new(JiraAccount.WithTokenInHand(Site, "brian@example.com", "a-token"), requester.Requester, clock);

    /// <summary>An account whose credential reference names an environment variable this process never sets, so resolving it always fails.</summary>
    private static JiraWriteExecutor ExecutorWithUnresolvableCredential(RecordingJiraRequester requester) =>
        new(
            new JiraAccount(Site, "brian@example.com", CredentialReference.EnvironmentVariable("HALL9K_TEST_JIRA_UNSET_TOKEN")),
            requester.Requester);

    private static JiraResponse Ok(string body) => new(200, body);

    private static JiraResponse Created(string body) => new(201, body);

    // ---- Create ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_sends_the_composed_description_unchanged_with_the_project_and_type_and_verifies_the_key()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Created("""{"id":"10001","key":"PROJ-1","self":"https://hall9k.atlassian.net/rest/api/2/issue/10001"}""")
                : Ok("""{"key":"PROJ-1"}"""));

        JiraWritePayload payload = new(
            "Dev Task",
            new Dictionary<string, string> { ["summary"] = "Fix the thing", ["description"] = "Some context." },
            Comment: null);

        JiraWriteResult result = await Executor(requester).CreateAsync(
            JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-1");

        JiraRequest create = requester.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        create.Url.Should().Be(new Uri("https://hall9k.atlassian.net/rest/api/2/issue"));
        create.JsonBody.Should().Contain("\"key\":\"PROJ\"")
            .And.Contain("\"name\":\"Dev Task\"")
            .And.Contain("Fix the thing");
        DescriptionSent(create).Should().Be("Some context.", "nothing is appended to the composed description");
        create.JsonBody.Should().NotContain("hall9k", "the card must not advertise the tool that made it");

        requester.Requests.Should().Contain(
            r => r.Method == HttpMethod.Get, "the created card is read back and verified before being trusted");
    }

    [Fact]
    public async Task Create_refuses_outright_when_no_board_is_bound_and_the_payload_names_none()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ =>
            throw new InvalidOperationException("must not reach Jira when no board is bound"));

        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "x" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.None, payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Kind.Should().Be(JiraWriteFailureKind.Other);
        requester.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_extracts_summary_and_description_case_insensitively_from_composed_fields()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-2"}""") : Ok("""{"key":"PROJ-2"}"""));

        JiraWritePayload payload = new(
            "Dev Task",
            new Dictionary<string, string> { ["Summary"] = "Cased summary", ["Description"] = "Cased body" },
            null);

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        JiraRequest create = requester.Requests.First(r => r.Method == HttpMethod.Post);
        create.JsonBody.Should().Contain("Cased summary").And.Contain("Cased body");
        // Only one summary/description pair reaches the request — the cased keys were consumed as
        // the first-class fields, not left behind to also ride along as ordinary custom fields.
        create.JsonBody!.Should().NotContain("\"Summary\"").And.NotContain("\"Description\"");
    }

    [Fact]
    public async Task Create_carries_a_custom_field_through_as_its_own_typed_json_value()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-3"}""") : Ok("""{"key":"PROJ-3"}"""));

        // A composed field's raw JSON text survives — a quoted numeric string stays a string
        // rather than being coerced into a bare number, the same fidelity FromJson preserves.
        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"S","customfield_10050":"10501"}}""");

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody
            .Should().Contain("\"customfield_10050\":\"10501\"");
    }

    [Fact]
    public async Task Create_a_null_valued_field_reaches_Jira_as_blank_rather_than_the_word_null()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-4"}""") : Ok("""{"key":"PROJ-4"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"S","description":null}}""");

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().NotContain("null");
        body.Should().NotContain("hall9k", "a blank description is sent blank, not filled with anything of ours");
        DescriptionSent(requester.Requests.First(r => r.Method == HttpMethod.Post)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_reported_success_with_no_key_in_the_response_is_refused_rather_than_guessed()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(201, "{}");
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*carried no key*");
    }

    [Fact]
    public async Task Create_an_auth_failure_from_the_readback_stays_classified_as_auth_failure()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Created("""{"key":"PROJ-5"}""")
                : new JiraResponse(401, """{"errorMessages":["denied"]}"""));

        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Kind.Should().Be(JiraWriteFailureKind.AuthFailure, "a create's own retry sweep must keep retrying");
    }

    [Fact]
    public async Task Create_a_non_auth_readback_failure_is_not_reported_as_a_refusal_of_the_write()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Created("""{"key":"PROJ-6"}""")
                : new JiraResponse(500, "boom"));

        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*do not record this as a refusal of the write*");
    }

    [Fact]
    public async Task Create_timing_out_after_the_request_was_sent_is_reported_as_genuinely_ambiguous()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => throw new TaskCanceledException());
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*may have been carried out*");
    }

    [Fact]
    public async Task Create_whose_connection_drops_after_sending_is_reported_as_genuinely_ambiguous_too()
    {
        // A connection reset or a response-read failure after the request was transmitted also
        // surfaces as HttpRequestException, not TaskCanceledException, and is exactly as ambiguous
        // as the timeout case above — Jira may have already carried the write out (independent
        // pre-PR review, adversarial lens, cycle 10).
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(
            _ => throw new HttpRequestException("connection reset"));
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*genuinely ambiguous*");
    }

    // ---- Update ------------------------------------------------------------------------------

    [Fact]
    public async Task Update_sends_only_the_named_fields_then_verifies_existence_only()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Put ? new JiraResponse(204, string.Empty) : Ok("""{"key":"PROJ-7"}"""));

        JiraWritePayload payload = new(null, new Dictionary<string, string> { ["summary"] = "New summary" }, null);
        JiraWriteResult result = await Executor(requester).UpdateAsync("PROJ-7", payload, CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-7");
        result.Summary.Should().Contain("does not re-read the changed field");

        JiraRequest update = requester.Requests.Single(r => r.Method == HttpMethod.Put);
        update.Url.Should().Be(new Uri("https://hall9k.atlassian.net/rest/api/2/issue/PROJ-7"));
        update.JsonBody.Should().Contain("New summary");
    }

    [Fact]
    public async Task Update_a_write_that_reports_success_but_reads_back_nothing_is_not_trusted()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Put ? new JiraResponse(204, string.Empty) : Ok("{}"));

        JiraWritePayload payload = new(null, new Dictionary<string, string> { ["summary"] = "New summary" }, null);
        Func<Task> act = () => Executor(requester).UpdateAsync("PROJ-8", payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*found nothing*");
    }

    // ---- Comment -----------------------------------------------------------------------------

    [Fact]
    public async Task Comment_posts_the_body_then_verifies_existence()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)
                ? Created("""{"id":"1"}""")
                : Ok("""{"key":"PROJ-9"}"""));

        JiraWriteResult result = await Executor(requester).CommentAsync("PROJ-9", "The PR merged.", "plain", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-9");
        JiraRequest comment = requester.Requests.Single(r => r.Url.ToString().EndsWith("/comment", StringComparison.Ordinal));
        comment.Method.Should().Be(HttpMethod.Post);
        comment.JsonBody.Should().Contain("The PR merged.");
    }

    [Fact]
    public async Task Comment_an_auth_failure_from_the_readback_is_reclassified_as_other_not_pending()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)
                ? Created("""{"id":"1"}""")
                : new JiraResponse(401, """{"errorMessages":["denied"]}"""));

        Func<Task> act = () => Executor(requester).CommentAsync("PROJ-10", "note", "plain", CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Kind.Should().Be(
            JiraWriteFailureKind.Other,
            "retrying automatically would post the identical comment a second time — a comment has no dedup gate");
        thrown.Message.Should().Contain("already landed");
    }

    // ---- Duplicate-card search (the create dedup gate) -------------------------------------------

    private static readonly JiraProjectKey Proj = JiraProjectKey.Parse("PROJ");

    private static string DescriptionSent(JiraRequest create)
    {
        using JsonDocument document = JsonDocument.Parse(create.JsonBody ?? "{}");
        return document.RootElement.GetProperty("fields").GetProperty("description").GetString() ?? string.Empty;
    }

    private static string SearchAnswer(params (string Key, string Summary)[] cards) =>
        new JsonObject
        {
            ["issues"] = new JsonArray([.. cards.Select(card => (JsonNode)new JsonObject
            {
                ["key"] = card.Key,
                ["fields"] = new JsonObject { ["summary"] = card.Summary },
            })]),
        }.ToJsonString();

    /// <summary>The first create request on the task was made twenty minutes before the search runs.</summary>
    private static readonly DateTimeOffset FirstRequestedAt = Now - TimeSpan.FromMinutes(20);

    [Fact]
    public async Task FindCreatedCardsAsync_finds_nothing_when_the_window_holds_no_card_with_the_summary()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200, SearchAnswer(("PROJ-1", "Something else entirely")));

        IReadOnlyList<string> found = await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        found.Should().BeEmpty();
        JiraRequest search = requester.Requests.Should().ContainSingle().Subject;
        search.Method.Should().Be(HttpMethod.Post);
        search.Url.Should().Be(new Uri("https://hall9k.atlassian.net/rest/api/3/search/jql"));
        using JsonDocument body = JsonDocument.Parse(search.JsonBody ?? "{}");
        string jql = body.RootElement.GetProperty("jql").GetString() ?? string.Empty;
        jql.Should().Contain("project = \"PROJ\"")
            .And.Contain("creator = currentUser()", "creator is always the login that made the card; reporter can be set through fields")
            .And.NotContain("reporter");
        body.RootElement.GetProperty("maxResults").GetInt32().Should().BeGreaterThan(JiraWriteExecutor.MaxWindowCards);
        body.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetString())
            .Should().Contain("summary", "the summary is read from the answer so no further call per candidate is needed");
    }

    [Fact]
    public async Task FindCreatedCardsAsync_bounds_the_window_five_minutes_before_the_first_request_in_relative_minutes()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, """{"issues":[]}""");

        await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        string jql = JsonDocument.Parse(requester.Requests.Single().JsonBody ?? "{}")
            .RootElement.GetProperty("jql").GetString() ?? string.Empty;
        jql.Should().Contain("created >= -25m", "twenty minutes since the first request plus the five-minute skew");
        jql.Should().NotMatchRegex(@"\d{4}-\d{2}-\d{2}", "a date literal is read in the Jira profile's timezone, a relative bound is not");
    }

    [Fact]
    public async Task FindCreatedCardsAsync_returns_the_matching_cards_earliest_first()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200,
            SearchAnswer(
                ("PROJ-4", "Fix the thing"),
                ("PROJ-5", "Fix the thing, differently"),
                ("PROJ-6", "Fix the thing"),
                ("PROJ-7", "fix the thing")));

        IReadOnlyList<string> found = await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        found.Should().Equal(["PROJ-4", "PROJ-6"], "only an exact summary matches, in the order Jira listed them (oldest first)");
    }

    /// <summary>
    /// A stand-in for Jira that applies the relative bound itself, so the test can say which card
    /// the window includes rather than only what the query text looked like.
    /// </summary>
    [Fact]
    public async Task FindCreatedCardsAsync_does_not_reach_a_same_summary_card_made_before_the_anchor_minus_five_minutes()
    {
        (string Key, string Summary, int MinutesAgo)[] board =
        [
            ("PROJ-1", "Fix the thing", 31),
            ("PROJ-2", "Fix the thing", 24),
        ];
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
        {
            using JsonDocument body = JsonDocument.Parse(request.JsonBody ?? "{}");
            string jql = body.RootElement.GetProperty("jql").GetString() ?? string.Empty;
            int window = int.Parse(Regex.Match(jql, @"created >= -(\d+)m").Groups[1].Value, CultureInfo.InvariantCulture);
            return Ok(SearchAnswer([.. board.Where(card => card.MinutesAgo <= window).Select(card => (card.Key, card.Summary))]));
        });

        IReadOnlyList<string> found = await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        found.Should().Equal(["PROJ-2"], "PROJ-1 predates the first request by more than the five-minute skew");
    }

    [Fact]
    public async Task FindCreatedCardsAsync_reads_a_busy_window_whole_rather_than_refusing_at_ten_cards()
    {
        (string, string)[] others = [.. Enumerable.Range(1, 40).Select(i => ($"PROJ-{i}", $"Another card {i}"))];
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200, SearchAnswer([.. others, ("PROJ-41", "Fix the thing")]));

        IReadOnlyList<string> found = await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        found.Should().Equal(["PROJ-41"]);
    }

    [Fact]
    public async Task FindCreatedCardsAsync_reads_a_window_of_exactly_the_cap_whole()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200,
            SearchAnswer([.. Enumerable.Range(1, JiraWriteExecutor.MaxWindowCards).Select(i => ($"PROJ-{i}", $"Card {i}"))]));

        IReadOnlyList<string> found = await Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task FindCreatedCardsAsync_refuses_a_window_holding_more_cards_than_the_cap()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200,
            SearchAnswer([.. Enumerable.Range(1, JiraWriteExecutor.MaxWindowCards + 1).Select(i => ($"PROJ-{i}", $"Card {i}"))]));

        Func<Task> act = () => Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Kind.Should().Be(JiraWriteFailureKind.Other);
        thrown.Message.Should().Contain("PROJ", "it names the board")
            .And.Contain("Fix the thing", "it names the summary")
            .And.Contain("2026-10-21 11:35 UTC", "it names the earliest time to look from");
    }

    [Fact]
    public async Task FindCreatedCardsAsync_refuses_when_Jira_says_there_is_another_page()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200, """{"issues":[],"isLast":false,"nextPageToken":"abc"}""");

        Func<Task> act = () => Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>()).WithMessage("*more than one search reads*");
    }

    [Theory]
    [InlineData("<html>not what this endpoint should answer with</html>")]
    [InlineData("""{"errorMessages":["nope"]}""")]
    [InlineData("""{"issues":[{"fields":{"summary":"Fix the thing"}}]}""")]
    [InlineData("""{"issues":[{"key":"PROJ-1"}]}""")]
    [InlineData("""{"issues":[{"key":"PROJ-1","fields":{"summary":null}}]}""")]
    [InlineData("""{"issues":[{"key":"not-a-key","fields":{"summary":"Fix the thing"}}]}""")]
    [InlineData("""{"issues":["PROJ-1"]}""")]
    public async Task FindCreatedCardsAsync_refuses_rather_than_creating_when_the_answer_or_an_entry_is_unreadable(string answer)
    {
        // A 2xx answer that is not the expected shape (an HTML interstitial from a corporate proxy,
        // a differently-shaped response, one entry that cannot be read) must not be read the same
        // way as a genuine {"issues": []}: collapsing the two would let a task that already has a
        // card get a second one filed on an unconfirmed check.
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, answer);

        Func<Task> act = () => Executor(requester, new FixedClock(Now))
            .FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Message.Should().Contain("PROJ").And.Contain("Fix the thing").And.Contain("2026-10-21 11:35 UTC");
    }

    // ---- Auth probe ----------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAuthenticationAsync_reports_Authenticated_on_success()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, """{"accountId":"5b10a2844c20165700ede21g","emailAddress":"brian@example.com"}""");

        JiraAuthProbeResult probe = await Executor(requester).ProbeAuthenticationAsync(CancellationToken.None);

        probe.Should().Be(JiraAuthProbeResult.Authenticated);
        JiraRequest sent = requester.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Get, "the probe confirms the credential with a GET, not a search POST");
        sent.Url.Should().Be(
            new Uri("https://hall9k.atlassian.net/rest/api/2/myself"),
            "the probe reads the credential's own identity rather than running a JQL search nothing on a real tenant matches");
    }

    [Fact]
    public async Task ProbeAuthenticationAsync_reports_AuthFailure_on_a_rejected_credential()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(401, """{"errorMessages":["denied"]}""");

        JiraAuthProbeResult probe = await Executor(requester).ProbeAuthenticationAsync(CancellationToken.None);

        probe.Should().Be(JiraAuthProbeResult.AuthFailure);
    }

    [Fact]
    public async Task ProbeAuthenticationAsync_reports_Unknown_on_any_other_refusal()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(500, "boom");

        JiraAuthProbeResult probe = await Executor(requester).ProbeAuthenticationAsync(CancellationToken.None);

        probe.Should().Be(JiraAuthProbeResult.Unknown);
    }

    /// <summary>
    /// A 2xx alone is not proof of a signed-in credential: an identity-aware proxy or an SSO portal
    /// in front of the tenant can answer the unauthenticated request with its own 200 and a login
    /// page, or a JSON body carrying no accountId — the same shape check
    /// <see cref="JiraWorkItemProvider.VerifyAccessAsync"/> already applies to this identical
    /// endpoint (independent pre-PR review, both lenses, cycle 8).
    /// </summary>
    [Fact]
    public async Task ProbeAuthenticationAsync_reports_Unknown_on_a_200_that_is_not_a_user_document()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, "<html>please sign in</html>");

        JiraAuthProbeResult probe = await Executor(requester).ProbeAuthenticationAsync(CancellationToken.None);

        probe.Should().Be(JiraAuthProbeResult.Unknown);
    }

    [Fact]
    public async Task ProbeAuthenticationAsync_reports_Unknown_on_a_200_json_body_missing_accountId()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            200, """{"error":"authentication required","login_url":"https://hall9k.atlassian.net/login"}""");

        JiraAuthProbeResult probe = await Executor(requester).ProbeAuthenticationAsync(CancellationToken.None);

        probe.Should().Be(JiraAuthProbeResult.Unknown);
    }

    [Fact]
    public async Task ProbeAuthenticationAsync_lets_an_unresolvable_credential_propagate_unwrapped()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, """{"accountId":"5b10a2844c20165700ede21g","emailAddress":"brian@example.com"}""");

        Func<Task> act = () => ExecutorWithUnresolvableCredential(requester).ProbeAuthenticationAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>(
            "a credential the vault cannot resolve is not a credential Jira rejected, and JiraDoctor's own "
            + "catch reports the vault's exact reason only if this propagates unwrapped rather than folding "
            + "into JiraAuthProbeResult.AuthFailure");
        requester.Requests.Should().BeEmpty("nothing reaches Jira before the credential is even resolved");
    }

    // ---- Ordinary 4xx / timeout on a read-only call is not ambiguous --------------------------

    [Fact]
    public async Task An_ordinary_4xx_refusal_is_reported_with_Jiras_own_message()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(
            400, """{"errorMessages":["The issue type is invalid."]}""");

        JiraWritePayload payload = new("Bogus", new Dictionary<string, string> { ["summary"] = "S" }, null);
        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Kind.Should().Be(JiraWriteFailureKind.Other);
        thrown.Message.Should().Contain("The issue type is invalid.");
    }

    [Fact]
    public async Task A_timeout_on_a_read_only_search_is_an_ordinary_failure_not_an_ambiguous_one()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => throw new TaskCanceledException());

        Func<Task> act = () => Executor(requester).FindCreatedCardsAsync(Proj, "Fix the thing", FirstRequestedAt, CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Kind.Should().Be(JiraWriteFailureKind.Other);
        thrown.Message.Should().NotContain("may have been carried out");
    }

    // ---- An unresolvable credential is a pending auth failure, not a raw domain exception --------

    [Fact]
    public async Task An_unresolvable_credential_is_reported_as_an_auth_failure_not_a_raw_domain_exception()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, "{}");
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => ExecutorWithUnresolvableCredential(requester)
            .CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Kind.Should().Be(
                JiraWriteFailureKind.AuthFailure,
                "a credential the vault cannot resolve must retry automatically the same way a 401 from Jira itself does");
        requester.Requests.Should().BeEmpty("nothing reaches Jira before the credential is even resolved");
    }

    // ---- A caller-supplied issue key is validated before it reaches a request URL -----------------

    [Theory]
    [InlineData("https://hall9k.atlassian.net/browse/PROJ-1")]
    [InlineData("PROJ-1/../PROJ-2")]
    [InlineData("not-a-key")]
    public async Task UpdateAsync_refuses_a_target_key_that_is_not_a_bare_PROJ_123_shape(string malformed)
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ =>
            throw new InvalidOperationException("must not reach Jira with an unvalidated key"));
        JiraWritePayload payload = new(null, new Dictionary<string, string> { ["summary"] = "New summary" }, null);

        Func<Task> act = () => Executor(requester).UpdateAsync(malformed, payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Kind.Should().Be(JiraWriteFailureKind.Other);
        requester.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CommentAsync_refuses_a_traversal_shaped_key_before_building_the_request_url()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ =>
            throw new InvalidOperationException("must not reach Jira with an unvalidated key"));

        Func<Task> act = () => Executor(requester).CommentAsync("PROJ-1/../PROJ-2", "note", "plain", CancellationToken.None);

        await act.Should().ThrowAsync<JiraWriteExecutionException>();
        requester.Requests.Should().BeEmpty();
    }

    // ---- A composed create cannot override the platform-resolved project or issue type -----------

    [Fact]
    public async Task Create_ignores_a_composed_project_or_issuetype_field_and_files_against_the_resolved_board()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-20"}""") : Ok("""{"key":"PROJ-20"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"S","project":{"key":"OTHER"},"issuetype":{"name":"Bug"}}}""");

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().Contain("\"project\":{\"key\":\"PROJ\"}");
        body.Should().Contain("\"issuetype\":{\"name\":\"Dev Task\"}");
    }

    [Fact]
    public async Task Create_ignores_a_composed_project_or_issuetype_field_regardless_of_its_casing()
    {
        // A composer's own casing choice ("issueType" as well as "issuetype") used to survive
        // AppendFields unremoved, since only ExtractField's own "summary"/"description" pulls were
        // case-insensitive — Jira then received both the composer's "issueType" and the executor's
        // own reserved "issuetype" node and refused the whole create as a duplicate field
        // (independent pre-PR review, adversarial lens, cycle 10).
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-20"}""") : Ok("""{"key":"PROJ-20"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"S","Project":{"key":"OTHER"},"issueType":{"name":"Bug"}}}""");

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().Contain("\"project\":{\"key\":\"PROJ\"}");
        body.Should().Contain("\"issuetype\":{\"name\":\"Dev Task\"}");
        body.Should().NotContain("OTHER");
        body.Should().NotContain("\"Bug\"");
    }

    [Fact]
    public async Task Update_refuses_a_composed_project_or_issuetype_field_rather_than_dropping_it_silently()
    {
        // JiraWritePayload.Validate already refuses a fresh payload shaped like this before it is
        // ever recorded — this simulates the one path that still reaches UpdateAsync unvalidated: a
        // write recorded pending under an older build that allowed the field through, replayed by
        // JiraWriteCoordinator.RetryPendingAsync, which does not re-validate a recorded payload
        // (independent pre-PR review, adversarial lens, cycle 13). Silently dropping it here — the
        // old behavior — let the read-back confirm the card still exists and record a verified
        // success for content that was never sent.
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => Ok("""{"key":"PROJ-20"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"fields":{"summary":"S","project":{"key":"OTHER"},"issuetype":{"name":"Bug"}}}""");

        Func<Task> act = () => Executor(requester).UpdateAsync("PROJ-20", payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Should().Match<JiraWriteExecutionException>(exception =>
                !exception.IsAuthFailure && exception.Message.Contains("\"project\""));
        requester.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_refuses_a_composed_issuetype_field_even_alongside_a_real_field()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => Ok("""{"key":"PROJ-20"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"fields":{"summary":"Renamed","issuetype":{"name":"Bug"}}}""");

        Func<Task> act = () => Executor(requester).UpdateAsync("PROJ-20", payload, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Should().Match<JiraWriteExecutionException>(exception =>
                !exception.IsAuthFailure && exception.Message.Contains("\"issuetype\""));
        requester.Requests.Should().BeEmpty();
    }

    // ---- A markdown-composed description or comment reaches Jira as wiki markup, not literal characters --

    [Fact]
    public async Task Create_converts_a_markdown_description_to_jira_wiki_markup_by_default()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post ? Created("""{"key":"PROJ-21"}""") : Ok("""{"key":"PROJ-21"}"""));

        JiraWritePayload payload = new(
            "Dev Task",
            new Dictionary<string, string> { ["summary"] = "S", ["description"] = "## Heading\n- one\n- two" },
            Comment: null);

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().Contain("h2. Heading").And.Contain("* one").And.Contain("* two");
        body.Should().NotContain("##");
    }

    [Fact]
    public async Task Comment_composed_as_plain_is_wrapped_in_a_noformat_block_so_it_renders_literally()
    {
        // Nothing tells Jira the format anymore once the transport moved off twg's own
        // --body-format flag, so "plain" text reaching Jira unconverted would have its own
        // wiki-markup-shaped characters (the "##" here) interpreted rather than shown literally
        // (independent pre-PR review, adversarial lens, cycle 1).
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)
                ? Created("""{"id":"1"}""")
                : Ok("""{"key":"PROJ-22"}"""));

        await Executor(requester).CommentAsync("PROJ-22", "## still literal", "plain", CancellationToken.None);

        string? body = requester.Requests.Single(r => r.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)).JsonBody;
        body.Should().Contain("{noformat}").And.Contain("## still literal");
    }

    [Fact]
    public async Task Comment_composed_as_plain_with_no_wiki_markup_active_character_is_posted_unwrapped_so_its_url_still_auto_links()
    {
        // Closeout's own merge comment is composed with format "plain" and carries a bare URL as
        // its one actionable element. Wrapping every "plain" body in {noformat} regardless of its
        // content boxed that URL into dead text (independent pre-PR review, adversarial lens,
        // cycle 2) — this is the case that must stay unwrapped for the URL to auto-link. The comment
        // below is the exact shape CloseoutEngine.MergeComment composes, GUID and hyphenated
        // "one-off" included, rather than a hyphen-free stand-in: a cycle-2 predicate keyed on bare
        // character membership passed this test while still boxing the real template, since the
        // template's own task-id GUID and its "one-off" wording both carry hyphens the stand-in
        // never exercised (independent pre-PR review, adversarial lens, cycle 3).
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)
                ? Created("""{"id":"1"}""")
                : Ok("""{"key":"PROJ-23"}"""));

        string comment = """
            The pull request for this work has merged: https://github.com/hall9k/hall9k/pull/1

            Recorded by Hall9k as task 28b19893-0000-4000-8000-000000000000 in project sample-project.
            This is a one-off note at merge — Hall9k does not change this item's status or close it,
            because which status a merge means is this project's workflow to decide.
            """;
        await Executor(requester).CommentAsync("PROJ-23", comment, "plain", CancellationToken.None);

        string? body = requester.Requests.Single(r => r.Url.ToString().EndsWith("/comment", StringComparison.Ordinal)).JsonBody;
        body.Should().NotContain("{noformat}").And.Contain("28b19893-0000-4000-8000-000000000000").And.Contain("one-off");
    }

    [Fact]
    public async Task Comment_composed_with_an_unsupported_format_is_refused_rather_than_posted_unconverted()
    {
        // "html" (and anything else) is refused at JiraWritePayload.Validate before a fresh write
        // is ever recorded — but JiraWriteCoordinator.RetryPendingAsync replays an already-recorded
        // payload without re-validating it, so a write recorded pending under an older build that
        // still allowed "html" would otherwise reach here and fall through to ToPlainLiteral, which
        // has no notion of HTML tags and would post the raw source verbatim (independent pre-PR
        // review, adversarial lens, cycle 10). This is the transport's own backstop for that case.
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => Ok("""{"key":"PROJ-24"}"""));

        Func<Task> act = () => Executor(requester).CommentAsync("PROJ-24", "<p>Hello</p>", "html", CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .Which.Should().Match<JiraWriteExecutionException>(exception =>
                !exception.IsAuthFailure && exception.Message.Contains("\"html\""));
        requester.Requests.Should().BeEmpty();
    }
}
