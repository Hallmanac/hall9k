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
            0, arguments.Contains("search") ? """{"key":"PROJ-123"}""" : """{"key":"PROJ-999"}""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["description"] = "Fixes the thing" }, null);

        TwgWriteResult result = await executor.CreateAsync(Project, payload, taskId, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.FileName.Should().Be("twg");
        create.Arguments.Should().ContainInOrder("jira", "create", "--project", "PROJ", "--type", "Dev Task");
        string description = create.Arguments.Should().ContainSingle(argument =>
            argument.StartsWith("description=", StringComparison.Ordinal)).Subject;
        description.Should().Contain("Fixes the thing").And.Contain(TwgJiraExecutor.Marker(taskId));

        twg.Calls.Should().Contain(call => call.Arguments.Contains("search"), "a create is always read back and verified");
    }

    [Fact]
    public async Task A_create_searches_for_the_tasks_marker_before_creating_anything()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(_ => new ProcessResult(0, "[]", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        await executor.FindByMarkerAsync(Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call = twg.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().ContainInOrder("jira", "search", "--jql");
        call.Arguments.Should().Contain(argument => argument.Contains("hall9k-task:"));
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
            0, arguments.Contains("search") ? """{"key":"PROJ-123"}""" : "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(null, new Dictionary<string, string> { ["customfield_1"] = "value" }, null);

        TwgWriteResult result = await executor.UpdateAsync("PROJ-123", payload, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        twg.Calls.Should().Contain(call => call.Arguments.Contains("update") && call.Arguments.Contains("customfield_1=value"));
        twg.Calls.Should().Contain(call => call.Arguments.Contains("search"));
    }

    [Fact]
    public async Task A_comment_never_transitions_or_closes_the_item()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("search") ? """{"key":"PROJ-123"}""" : "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        await executor.CommentAsync("PROJ-123", "The pull request merged.", "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            twg.Calls.Should().Contain(c => c.Arguments.Contains("--comment")).Subject;
        call.Arguments.Should().NotContain("transition");
        call.Arguments.Should().NotContain(argument => argument.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_write_that_reports_success_but_reads_back_nothing_is_not_trusted()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Succeeding("[]");
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "/repo", CancellationToken.None);

        (await comment.Should().ThrowAsync<TwgExecutionException>())
            .Which.Message.Should().Contain("reading it back to verify found nothing");
    }

    [Theory]
    [InlineData("Error: not authenticated. Run 'twg login' to continue.")]
    [InlineData("HTTP 401 Unauthorized")]
    [InlineData("the stored token has expired")]
    public async Task An_expired_or_missing_login_is_classified_apart_from_every_other_refusal(string stderr)
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Failing(stderr);
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeTrue();
        exception.Message.Should().Contain("twg login");
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_authentication_is_not_misread_as_one()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.Failing("field 'customfield_10010' is required");
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "/repo", CancellationToken.None);

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

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "/repo", CancellationToken.None);

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

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Kind.Should().Be(TwgFailureKind.Other);
        exception.IsAuthFailure.Should().BeFalse();
        exception.Message.Should().Contain("command line was too long");
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
            RecordingProcessRunner.Failing("run 'twg login'").Runner)
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
