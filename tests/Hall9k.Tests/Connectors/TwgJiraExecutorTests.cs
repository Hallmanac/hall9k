using System.ComponentModel;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// hall9k's sole path to writing Jira (Brian's design, 2026-08-28), tested against a recorded
/// twg instead of a live one: argument construction for create/update/comment, the marker-based
/// dedup search run before every create, the mandatory read-back verification, and — the part
/// that matters most for the "expired login is a handled state" acceptance criterion — that an
/// authentication refusal is classified apart from every other failure and a missing binary apart
/// from both.
/// </summary>
public sealed class TwgJiraExecutorTests
{
    private static readonly JiraProjectKey Project = JiraProjectKey.Parse("PROJ");

    [Fact]
    public async Task A_create_embeds_the_tasks_marker_and_verifies_the_returned_key()
    {
        Guid taskId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["description"] = "Fixes the thing" }, null);

        TwgWriteResult result = await executor.CreateAsync(Project, payload, taskId, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.FileName.Should().Be("twg");
        create.Arguments.Should().ContainInOrder("jira", "workitem", "create", "--space", "PROJ", "--type", "Dev Task");
        string description = create.Arguments.SkipWhile(argument => argument != "--description").Skip(1).First();
        description.Should().Contain("Fixes the thing").And.Contain(TwgJiraExecutor.Marker(taskId));

        twg.Calls.Should().Contain(call => call.Arguments.Contains("query"), "a create is always read back and verified");
    }

    /// <summary>
    /// Every call is told the registered connection's tenant explicitly rather than left to
    /// whatever twg's own ambient auth.conf/TWG_SITE resolves to on the machine running it — a
    /// mismatch there used to have the write and its own read-back both silently hit the wrong
    /// tenant (independent pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public async Task Every_call_names_the_registered_connections_site_explicitly()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner, new Uri("https://your-org.atlassian.net"));
        JiraWritePayload payload = new("Dev Task", null, null);

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        twg.Calls.Should().OnlyContain(call => call.Arguments.Contains("--site") && call.Arguments.Contains("your-org.atlassian.net"));
    }

    /// <summary>A caller with no resolvable connection gets twg's own ambient default rather than a fabricated site.</summary>
    [Fact]
    public async Task No_site_is_passed_when_none_is_given()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(_ => new ProcessResult(0, "[]", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        await executor.FindByMarkerAsync(Guid.NewGuid(), "/repo", CancellationToken.None);

        twg.Calls.Should().OnlyContain(call => !call.Arguments.Contains("--site"));
    }

    /// <summary>
    /// A composed description or comment is told to twg explicitly as markdown by default, since
    /// twg's own default (html) mangles the headings/bullets/Given-When-Then blocks a project's
    /// card-authoring skills actually produce (independent pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public async Task A_create_defaults_to_markdown_when_the_payload_names_no_format()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new("Dev Task", new Dictionary<string, string> { ["description"] = "## Heading" }, null);

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.Arguments.Should().ContainInOrder("--description-format", "markdown");
    }

    /// <summary>A payload that names a format explicitly (closeout's own plain-text merge comment, for one) has it honored rather than overridden.</summary>
    [Fact]
    public async Task A_named_format_is_passed_through_instead_of_the_default()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["description"] = "plain text" }, null, Format: "plain");

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.Arguments.Should().ContainInOrder("--description-format", "plain");
    }

    /// <summary>
    /// A composed payload's own casing of a first-class field should not survive as a second,
    /// marker-only <c>--field</c> alongside twg's own <c>--description</c> (independent pre-PR
    /// review, cycle 1, low-severity ride-along).
    /// </summary>
    [Fact]
    public async Task A_differently_cased_description_field_is_not_sent_twice()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["Description"] = "Fixes the thing" }, null);

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.Arguments.Should().Contain("--description");
        create.Arguments.Should().NotContain(argument => argument.StartsWith("Description=", StringComparison.Ordinal));
        create.Arguments.Should().NotContain(argument => argument.StartsWith("description=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_create_searches_for_the_tasks_marker_before_creating_anything()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(_ => new ProcessResult(0, "[]", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        await executor.FindByMarkerAsync(Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call = twg.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().ContainInOrder("jira", "workitem", "query", "--jql");
        call.Arguments.Should().Contain(argument => argument.Contains("hall9k-task:"));
    }

    /// <summary>
    /// twg's own <c>--output json</c> never prints raw JSON to stdout: every call carries a YAML
    /// summary envelope naming a temp file the real payload was written to, and a query nests its
    /// results under <c>data.issues</c> rather than at the root (independent pre-PR review, cycle
    /// 1, verified against an installed, authenticated twg). This is the shape a bare-JSON fixture
    /// cannot exercise, so it is asserted against the real envelope text and a real temp file.
    /// </summary>
    [Fact]
    public async Task A_search_reads_the_key_out_of_twgs_real_envelope_and_file_shape()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, """{"apiVersion":"v2","command":"jira.workitem.query","data":{"issues":[{"key":"PROJ-777","summary":"Found it"}]}}""");
            string envelope =
                $"""
                output_files:
                  stdout: "{file}"
                  compact: "{file}.compact"
                command: "jira.workitem.query"
                agent_output:
                  summary: "stats"
                ---END---
                """;
            RecordingProcessRunner twg = RecordingProcessRunner.Succeeding(envelope);
            TwgJiraExecutor executor = new(twg.Runner);

            string? found = await executor.FindByMarkerAsync(Guid.NewGuid(), "/repo", CancellationToken.None);

            found.Should().Be("PROJ-777");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The marker survives across attempts because it is keyed to the task rather than to any one
    /// write's own guid — a fresh write id every attempt means a per-write marker could never be
    /// found by the retry it exists to protect (independent pre-PR review, cycle 1).
    /// </summary>
    [Fact]
    public async Task The_same_tasks_marker_is_found_by_a_later_attempt_with_a_different_write_id()
    {
        Guid taskId = Guid.NewGuid();
        RecordingProcessRunner twg = RecordingProcessRunner.Succeeding("""[{"key":"PROJ-999"}]""");
        TwgJiraExecutor executor = new(twg.Runner);

        string? foundByFirstAttempt = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);
        string? foundByRetry = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        foundByFirstAttempt.Should().Be("PROJ-999");
        foundByRetry.Should().Be("PROJ-999");
        twg.Calls.Should().OnlyContain(
            call => call.Arguments.Any(argument => argument.Contains(TwgJiraExecutor.Marker(taskId))));
    }

    [Fact]
    public async Task A_dedup_hit_is_read_as_the_card_an_earlier_attempt_already_made()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Succeeding("""[{"key":"PROJ-999"}]""");
        TwgJiraExecutor executor = new(twg.Runner);

        string? found = await executor.FindByMarkerAsync(Guid.NewGuid(), "/repo", CancellationToken.None);

        found.Should().Be("PROJ-999");
    }

    [Fact]
    public async Task An_update_writes_the_fields_and_then_verifies_by_reading_back()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            null, new Dictionary<string, string> { ["customfield_1"] = "value", ["description"] = "## Heading" }, null);

        TwgWriteResult result = await executor.UpdateAsync("PROJ-123", payload, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) update =
            twg.Calls.Should().Contain(call =>
                call.Arguments.Contains("update") && call.Arguments.Contains("--id") && call.Arguments.Contains("customfield_1=value")).Subject;
        update.Arguments.Should().ContainInOrder("--description-format", "markdown");
        twg.Calls.Should().Contain(call => call.Arguments.Contains("query"));
    }

    [Fact]
    public async Task A_comment_never_transitions_or_closes_the_item()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("query") ? """{"key":"PROJ-123"}""" : "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        await executor.CommentAsync("PROJ-123", "The pull request merged.", "plain", "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            twg.Calls.Should().Contain(c => c.Arguments.Contains("--body")).Subject;
        call.Arguments.Should().ContainInOrder("jira", "workitem", "comment", "create", "--issue-id", "PROJ-123");
        call.Arguments.Should().ContainInOrder("--body-format", "plain");
        call.Arguments.Should().NotContain("transition");
        call.Arguments.Should().NotContain(argument => argument.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_write_that_reports_success_but_reads_back_nothing_is_not_trusted()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Succeeding("[]");
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        (await comment.Should().ThrowAsync<TwgExecutionException>())
            .Which.Message.Should().Contain("reading it back to verify found nothing");
    }

    /// <summary>
    /// twg's real shape for an expired or missing login (verified live against an installed twg,
    /// independent pre-PR review, cycle 3): the JSON error envelope — <c>error.code</c>
    /// <c>AUTH_REQUIRED</c>, <c>error.message</c> "authentication required" — lands on stdout at
    /// exit 77, with stderr left empty. None of the old stderr-substring checks this class used
    /// to run could ever have matched that message on that stream.
    /// </summary>
    [Fact]
    public async Task An_expired_or_missing_login_is_classified_from_twgs_real_stdout_envelope()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.TwgAuthExpired();
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeTrue();
        exception.Message.Should().Contain("twg login");
        exception.Message.Should().Contain("authentication required");
    }

    /// <summary>
    /// The stderr substring classification stays as a fallback for a refusal that never reaches
    /// twg's own JSON envelope (a spawn-level or transport-level failure outside twg's control),
    /// even though a genuine twg auth refusal is never actually shaped this way.
    /// </summary>
    [Theory]
    [InlineData("Error: not authenticated. Run 'twg login' to continue.")]
    [InlineData("HTTP 401 Unauthorized")]
    [InlineData("the stored token has expired")]
    public async Task A_stderr_only_refusal_still_falls_back_to_substring_classification(string stderr)
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Failing(stderr);
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeTrue();
        exception.Message.Should().Contain("twg login");
    }

    /// <summary>
    /// The envelope's message is twg's and Jira's text rather than Hall9k's — a refusal routinely
    /// quotes a composed field value or a card's own adopted text straight back — and it reaches
    /// both a terminal (<c>write-jira</c> writes the exception message to stderr) and the event
    /// stream (<c>JiraWriteFailed.Reason</c>) with no escaping in between. It goes through the
    /// same <c>RelayedText.OneLine</c> sanitisation the stderr branch has always applied
    /// (independent pre-PR review, cycle 4).
    /// </summary>
    [Fact]
    public async Task Relayed_text_from_the_error_envelope_is_sanitised_the_way_stderr_always_was()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.FailingWithEnvelope(
            1,
            "FIELD_INVALID",
            @"summary \u001b[31mrejected\u001b[0m\rby Jira\nand its second line");
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().NotContain("\u001b");
        exception.Message.Should().NotContain("\r");
        exception.Message.Should().NotContain("\n");
        exception.Message.Should().Contain("summary [31mrejected[0mby Jira and its second line");
    }

    /// <summary>
    /// A refusal with nothing readable on either stream must not render as the empty-tailed "twg
    /// refused the write: " — the fixed message falls back to naming that nothing was said rather
    /// than trailing off (independent pre-PR review, cycle 3).
    /// </summary>
    [Fact]
    public async Task A_refusal_with_no_readable_detail_does_not_trail_off_empty()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(_ => new ProcessResult(1, string.Empty, string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Kind.Should().Be(TwgFailureKind.Other);
        exception.Message.Should().NotBe("twg refused the write: ");
        exception.Message.Should().Contain("nothing at all");
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_authentication_is_not_misread_as_one()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Failing("field 'customfield_10010' is required");
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeFalse();
        exception.Kind.Should().Be(TwgFailureKind.Other);
    }

    [Fact]
    public async Task A_missing_twg_binary_is_told_apart_from_an_expired_login()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Unstartable(
            new Win32Exception("No such file or directory"));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Kind.Should().Be(TwgFailureKind.MissingBinary);
        exception.IsAuthFailure.Should().BeFalse();
    }

    /// <summary>
    /// A long composed description or comment can push the whole command line over the OS's own
    /// limit, and .NET reports that refused spawn with the identical exception type a missing
    /// binary throws. Windows reports ERROR_FILENAME_EXCED_RANGE (206); this asserts the fix
    /// treats that distinctly rather than telling an operator to install a twg that is already
    /// there (independent pre-PR review, cycle 5).
    /// </summary>
    [Fact]
    public async Task A_command_line_refused_for_its_length_is_not_misread_as_a_missing_binary()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Unstartable(
            new Win32Exception(206, "The filename or extension is too long"));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Kind.Should().Be(TwgFailureKind.Other);
        exception.IsAuthFailure.Should().BeFalse();
        exception.Message.Should().Contain("command line was too long");
    }

    /// <summary>
    /// An exit-0 stuck output means twg itself reported success before something it started held
    /// the pipe open — the write was very likely carried out, so the message must not read like
    /// the plain "did not answer" case a genuine hang produces (independent pre-PR review, cycle
    /// 1, both lenses, mirroring GitHubWorkItemProvider.RunGhAsync's own onOutputStuckAfterSuccess
    /// distinction).
    /// </summary>
    [Fact]
    public async Task An_exit_zero_stuck_output_is_reported_as_a_likely_success_not_a_hang()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.ExitedButOutputStuck(0);
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().Contain("very likely carried out");
    }

    [Fact]
    public async Task A_nonzero_exit_stuck_output_is_reported_as_a_real_failure()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.ExitedButOutputStuck(1);
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().NotContain("very likely carried out");
    }

    [Fact]
    public async Task The_doctor_probe_reads_each_outcome_distinctly()
    {
        TwgAuthProbeResult authenticated = await new TwgJiraExecutor(RecordingProcessRunner.Succeeding("[]").Runner)
            .ProbeAuthenticationAsync("/repo", CancellationToken.None);
        authenticated.Should().Be(TwgAuthProbeResult.Authenticated);

        TwgAuthProbeResult missing = await new TwgJiraExecutor(
            RecordingProcessRunner.Unstartable(new Win32Exception("not found")).Runner)
            .ProbeAuthenticationAsync("/repo", CancellationToken.None);
        missing.Should().Be(TwgAuthProbeResult.MissingBinary);

        TwgAuthProbeResult expired = await new TwgJiraExecutor(
            RecordingProcessRunner.TwgAuthExpired().Runner)
            .ProbeAuthenticationAsync("/repo", CancellationToken.None);
        expired.Should().Be(TwgAuthProbeResult.AuthExpired);
    }

    [Fact]
    public void A_composed_payload_round_trips_through_json_the_write_surface_reads()
    {
        JiraWritePayload payload = new(
            "Dev Task",
            new Dictionary<string, string> { ["customfield_10010"] = "Value" },
            "A comment",
            "DEV");

        JiraWritePayload roundTripped = JiraWritePayload.FromJson(payload.ToJson());

        roundTripped.WorkItemType.Should().Be("Dev Task");
        roundTripped.Fields.Should().ContainKey("customfield_10010").WhoseValue.Should().Be("Value");
        roundTripped.Comment.Should().Be("A comment");
        roundTripped.ProjectKey.Should().Be("DEV");
    }
}
