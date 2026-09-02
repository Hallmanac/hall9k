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
    private static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static JiraWriteExecutor Executor(RecordingJiraRequester requester) =>
        new(JiraAccount.WithTokenInHand(Site, "brian@example.com", "a-token"), requester.Requester);

    /// <summary>An account whose credential reference names an environment variable this process never sets, so resolving it always fails.</summary>
    private static JiraWriteExecutor ExecutorWithUnresolvableCredential(RecordingJiraRequester requester) =>
        new(
            new JiraAccount(Site, "brian@example.com", CredentialReference.EnvironmentVariable("HALL9K_TEST_JIRA_UNSET_TOKEN")),
            requester.Requester);

    private static JiraResponse Ok(string body) => new(200, body);

    private static JiraResponse Created(string body) => new(201, body);

    // ---- Create ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_embeds_the_marker_sends_the_project_and_type_and_verifies_the_key()
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
            JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-1");

        JiraRequest create = requester.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        create.Url.Should().Be(new Uri("https://hall9k.atlassian.net/rest/api/2/issue"));
        create.JsonBody.Should().Contain("\"key\":\"PROJ\"")
            .And.Contain("\"name\":\"Dev Task\"")
            .And.Contain("Fix the thing")
            .And.Contain($"hall9k-task:{TaskId:D}");

        requester.Requests.Should().Contain(
            r => r.Method == HttpMethod.Get, "the created card is read back and verified before being trusted");
    }

    [Fact]
    public async Task Create_refuses_outright_when_no_board_is_bound_and_the_payload_names_none()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ =>
            throw new InvalidOperationException("must not reach Jira when no board is bound"));

        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "x" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.None, payload, TaskId, CancellationToken.None);

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

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().NotContain("null");
        body.Should().Contain($"hall9k-task:{TaskId:D}", "a blank description still carries the dedup marker");
        body.Should().NotContain($"[hall9k-task:{TaskId:D}]", "brackets are Jira's own link notation and would turn the marker into an unresolvable link");
    }

    [Fact]
    public async Task Create_reported_success_with_no_key_in_the_response_is_refused_rather_than_guessed()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(201, "{}");
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*do not record this as a refusal of the write*");
    }

    [Fact]
    public async Task Create_timing_out_after_the_request_was_sent_is_reported_as_genuinely_ambiguous()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => throw new TaskCanceledException());
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["summary"] = "S" }, null);

        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*may have been carried out*");
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

    // ---- Marker search (the dedup gate) -------------------------------------------------------

    [Fact]
    public async Task FindByMarkerAsync_returns_null_when_the_search_comes_back_empty()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.Succeeding(200, """{"issues":[]}""");

        string? found = await Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        found.Should().BeNull();
        JiraRequest search = requester.Requests.Should().ContainSingle().Subject;
        search.Url.Should().Be(new Uri("https://hall9k.atlassian.net/rest/api/3/search/jql"));
        search.JsonBody.Should().Contain($"hall9k-task:{TaskId:D}");
    }

    [Fact]
    public async Task FindByMarkerAsync_confirms_the_marker_from_the_candidates_own_description_field()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Ok("""{"issues":[{"key":"PROJ-11"},{"key":"PROJ-12"}]}""")
                : request.Url.ToString().Contains("PROJ-11")
                    // A token-overlapping false positive: this candidate's description mentions
                    // "task" and a similar-looking id, but not the exact marker text.
                    ? Ok("""{"fields":{"description":"unrelated task:99999999-9999-9999-9999-999999999999"}}""")
                    : Ok($"{{\"fields\":{{\"description\":\"[hall9k-task:{TaskId:D}]\"}}}}"));

        string? found = await Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        found.Should().Be("PROJ-12", "the first candidate's tokens merely overlapped; only the second actually carries the marker");
    }

    [Fact]
    public async Task FindByMarkerAsync_refuses_a_full_page_with_none_confirmed()
    {
        string issues = string.Join(",", Enumerable.Range(1, 10).Select(i => $$"""{"key":"PROJ-{{i}}"}"""));
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Ok($$"""{"issues":[{{issues}}]}""")
                : Ok("""{"fields":{"description":"nothing to see here"}}"""));

        Func<Task> act = () => Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*full page*");
    }

    [Fact]
    public async Task FindByMarkerAsync_refuses_rather_than_creating_when_the_search_answer_is_unreadable()
    {
        // A 2xx answer that is not the expected {"issues": [...]} shape (an HTML interstitial from
        // a corporate proxy, a blank body, a differently-shaped response) must not be read the same
        // way as a genuine {"issues": []} — collapsing the two would let a task that already has a
        // card get a second one filed on an unconfirmed dedup check (independent pre-PR review,
        // both lenses, cycle 1).
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Ok("<html>not what this endpoint should answer with</html>")
                : throw new InvalidOperationException("must not confirm any candidate when the search itself could not be read"));

        Func<Task> act = () => Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*could not be confirmed readable*");
    }

    [Fact]
    public async Task FindByMarkerAsync_refuses_rather_than_creating_when_a_candidates_own_readback_is_unreadable()
    {
        // The search itself answered readably and named a real candidate, but that candidate's own
        // description read-back came back unreadable — this must not be read as "this candidate
        // does not carry the marker" (which would let the search move past it, and possibly file a
        // duplicate), the same doctrine the search's own full-page and unreadable cases already get.
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Ok("""{"issues":[{"key":"PROJ-13"}]}""")
                : Ok("not json at all"));

        Func<Task> act = () => Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        (await act.Should().ThrowAsync<JiraWriteExecutionException>())
            .WithMessage("*could not be read back to confirm*");
    }

    [Fact]
    public async Task FindByMarkerAsync_treats_a_candidate_with_no_description_field_as_readable_and_not_carrying_the_marker()
    {
        // A card genuinely having no description (the field absent, or JSON null) is a real,
        // observed fact, not an unconfirmable read — it must not be refused the same way a
        // malformed body is.
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
            request.Method == HttpMethod.Post
                ? Ok("""{"issues":[{"key":"PROJ-14"}]}""")
                : Ok("""{"fields":{"description":null}}"""));

        string? found = await Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

        found.Should().BeNull();
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
        Func<Task> act = () => Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        JiraWriteExecutionException thrown = (await act.Should().ThrowAsync<JiraWriteExecutionException>()).Which;
        thrown.Kind.Should().Be(JiraWriteFailureKind.Other);
        thrown.Message.Should().Contain("The issue type is invalid.");
    }

    [Fact]
    public async Task A_timeout_on_a_read_only_search_is_an_ordinary_failure_not_an_ambiguous_one()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => throw new TaskCanceledException());

        Func<Task> act = () => Executor(requester).FindByMarkerAsync(TaskId, CancellationToken.None);

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
            .CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Post).JsonBody;
        body.Should().Contain("\"project\":{\"key\":\"PROJ\"}");
        body.Should().Contain("\"issuetype\":{\"name\":\"Dev Task\"}");
    }

    [Fact]
    public async Task Update_drops_a_composed_project_or_issuetype_field_rather_than_sending_it()
    {
        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(_ => Ok("""{"key":"PROJ-20"}"""));

        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"fields":{"summary":"S","project":{"key":"OTHER"},"issuetype":{"name":"Bug"}}}""");

        await Executor(requester).UpdateAsync("PROJ-20", payload, CancellationToken.None);

        string? body = requester.Requests.First(r => r.Method == HttpMethod.Put).JsonBody;
        body.Should().NotContain(
            "\"project\"", "an update has no bound project of its own to overwrite a composed one with, so it is dropped instead");
        body.Should().NotContain(
            "\"issuetype\"", "an update never moves a card to a different work item type through a field write");
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

        await Executor(requester).CreateAsync(JiraProjectKey.Parse("PROJ"), payload, TaskId, CancellationToken.None);

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
}
