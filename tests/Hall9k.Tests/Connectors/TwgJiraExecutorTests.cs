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

    /// <summary>
    /// twg's real <c>jira workitem create</c> answer, verified against the installed binary's own
    /// create implementation (independent pre-PR review, cycle 6): the new card's key sits at
    /// <c>data.issue.key</c>, an object-valued property — not a root-level "key", the unrealistic
    /// shape this fixture used to model, which hid <c>FindEntity</c> never descending into an
    /// object to find it.
    /// </summary>
    private const string RealisticCreateAnswer =
        """{"apiVersion":"v2","command":"jira.workitem.create","data":{"success":true,"issue":{"id":"10001","key":"PROJ-999","self":"https://your-org.atlassian.net/rest/api/3/issue/10001"}}}""";

    /// <summary>
    /// twg's real <c>jira workitem get</c> answer for a single key, verified against the installed
    /// binary directly (independent pre-PR review, cycle 7): the card is the sole element of a
    /// <c>data</c> array, not an object sitting directly under <c>data</c> — the shape this fixture
    /// used to model before that verification.
    /// </summary>
    private static string RealisticGetAnswer(string key) =>
        $"{{\"apiVersion\":\"v2\",\"command\":\"jira.workitem.get\",\"data\":[{{\"key\":\"{key}\",\"summary\":\"Found it\"}}]}}";

    /// <summary>
    /// twg's real <c>jira workitem get --fields description</c> answer shape: the description
    /// comes back as Atlassian Document Format, a JSON tree, with the plain text — the marker
    /// included — sitting inside nested "text" nodes rather than as a flat string (verified
    /// against the installed binary directly, independent pre-PR review, conformance and
    /// adversarial lenses, cycle 1).
    /// </summary>
    private static string RealisticDescriptionGetAnswer(string key, string descriptionText) =>
        """{"apiVersion":"v2","command":"jira.workitem.get","data":[{"key":"@KEY@","description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"@TEXT@"}]}]}}]}"""
            .Replace("@KEY@", key)
            .Replace("@TEXT@", descriptionText);

    [Fact]
    public async Task A_create_embeds_the_tasks_marker_and_verifies_the_returned_key()
    {
        Guid taskId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["summary"] = "Fixes it", ["description"] = "Fixes the thing" }, null);

        TwgWriteResult result = await executor.CreateAsync(Project, payload, taskId, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.FileName.Should().Be("twg");
        create.Arguments.Should().ContainInOrder("jira", "workitem", "create", "--space", "PROJ", "--type", "Dev Task");
        string description = create.Arguments.SkipWhile(argument => argument != "--description").Skip(1).First();
        description.Should().Contain("Fixes the thing").And.Contain(TwgJiraExecutor.Marker(taskId));

        twg.Calls.Should().Contain(call => call.Arguments.Contains("get"), "a create is always read back and verified");
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
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
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
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
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
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
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
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
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

    /// <summary>
    /// A JSON <c>null</c> field value decodes to blank rather than the literal text "null" — the
    /// same rule <see cref="JiraWritePayload"/>'s own copy of this method already applies before
    /// validation, but this copy is what actually reaches twg's <c>--summary</c> flag, so a
    /// null-valued summary that cleared validation on the strength of some other field must not
    /// still send the four-character word "null" as the card's real title (independent pre-PR
    /// review, adversarial lens, cycle 8).
    /// </summary>
    [Fact]
    public async Task A_null_valued_field_decodes_to_blank_rather_than_the_literal_word_null()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            "Dev Task", new Dictionary<string, string> { ["summary"] = "null", ["description"] = "Real body" }, null);

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.Arguments.Should().NotContain("--summary", "a blank summary must not be sent at all");
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
        Guid taskId = Guid.NewGuid();
        string file = Path.GetTempFileName();
        try
        {
            // The same file content stands in for both twg calls this dedup hit makes: the
            // query's own answer (read for "key") and the confirmation get's answer (read for
            // the marker text) — a real envelope only needs to carry both once.
            string queryAnswer =
                """{"apiVersion":"v2","command":"jira.workitem.query","data":{"issues":[{"key":"PROJ-777","summary":"Found it","description":"@MARKER@"}]}}"""
                    .Replace("@MARKER@", TwgJiraExecutor.Marker(taskId));
            await File.WriteAllTextAsync(file, queryAnswer);
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

            string? found = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

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
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(0, RealisticDescriptionGetAnswer("PROJ-999", TwgJiraExecutor.Marker(taskId)), string.Empty)
            : new ProcessResult(0, """[{"key":"PROJ-999"}]""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        string? foundByFirstAttempt = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);
        string? foundByRetry = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        foundByFirstAttempt.Should().Be("PROJ-999");
        foundByRetry.Should().Be("PROJ-999");
        twg.Calls.Where(call => call.Arguments.Contains("query")).Should().OnlyContain(
            call => call.Arguments.Any(argument => argument.Contains(TwgJiraExecutor.Marker(taskId))));
        twg.Calls.Where(call => call.Arguments.Contains("get")).Should().OnlyContain(
            call => call.Arguments.Contains("PROJ-999"));
    }

    /// <summary>
    /// The JQL search alone is not proof of identity — it is fuzzy, tokenized text matching, not
    /// equality — so a dedup hit is only trusted once the candidate's own description is read
    /// back and confirmed to actually carry this task's exact marker (independent pre-PR review,
    /// conformance and adversarial lenses, cycle 1).
    /// </summary>
    [Fact]
    public async Task A_dedup_hit_is_read_as_the_card_an_earlier_attempt_already_made()
    {
        Guid taskId = Guid.NewGuid();
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(0, RealisticDescriptionGetAnswer("PROJ-999", TwgJiraExecutor.Marker(taskId)), string.Empty)
            : new ProcessResult(0, """[{"key":"PROJ-999"}]""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        string? found = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        found.Should().Be("PROJ-999");
    }

    /// <summary>
    /// The physical dedup gate's own defect this fixes (independent pre-PR review, conformance
    /// and adversarial lenses, cycle 1): the JQL search can return a candidate whose tokens merely
    /// overlap this task's marker — a different task's own card — and trusting that match without
    /// reading the candidate back would silently link this task to someone else's card.
    /// </summary>
    [Fact]
    public async Task A_search_hit_that_does_not_actually_carry_the_marker_is_not_trusted()
    {
        Guid taskId = Guid.NewGuid();
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(0, RealisticDescriptionGetAnswer("PROJ-999", TwgJiraExecutor.Marker(Guid.NewGuid())), string.Empty)
            : new ProcessResult(0, """[{"key":"PROJ-999"}]""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        string? found = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        found.Should().BeNull("the candidate's own description carries a different task's marker, not this one's");
    }

    /// <summary>
    /// The fix for the defect above's sibling (independent pre-PR review, adversarial lens, cycle
    /// 1): the search can return several candidates with no ordering guarantee, so a token-overlap
    /// false match sorting ahead of the real card must not stop the gate from confirming the rest
    /// of the search's own results.
    /// </summary>
    [Fact]
    public async Task A_second_search_hit_carrying_the_marker_is_found_when_the_first_does_not_carry_it()
    {
        Guid taskId = Guid.NewGuid();
        Guid otherTaskId = Guid.NewGuid();
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments =>
        {
            if (arguments.Contains("PROJ-111"))
            {
                return new ProcessResult(0, RealisticDescriptionGetAnswer("PROJ-111", TwgJiraExecutor.Marker(otherTaskId)), string.Empty);
            }

            if (arguments.Contains("PROJ-222"))
            {
                return new ProcessResult(0, RealisticDescriptionGetAnswer("PROJ-222", TwgJiraExecutor.Marker(taskId)), string.Empty);
            }

            return new ProcessResult(0, """[{"key":"PROJ-111"},{"key":"PROJ-222"}]""", string.Empty);
        });
        TwgJiraExecutor executor = new(twg.Runner);

        string? found = await executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        found.Should().Be(
            "PROJ-222", "the first hit's tokens merely overlapped a different task's marker; the second hit is this task's real card");
    }

    /// <summary>
    /// The full-page defect this fixes (independent pre-PR review, adversarial lens, cycle 4): the
    /// query is capped at <c>MaxMarkerSearchCandidates</c> (10) candidates, and a search that comes
    /// back exactly full — every one of the 10 confirmed clear — must not read as "no marker
    /// anywhere", because the real marker-carrying card could be ranked eleventh and never reached
    /// the search at all. This must refuse rather than return null, the same "an unconfirmable
    /// check refuses rather than guesses" doctrine already applied to a single unreadable
    /// candidate.
    /// </summary>
    [Fact]
    public async Task A_full_page_of_non_matching_candidates_refuses_rather_than_reads_as_no_marker()
    {
        Guid taskId = Guid.NewGuid();
        Guid otherTaskId = Guid.NewGuid();
        string[] keys = Enumerable.Range(1, 10).Select(index => $"PROJ-{index}").ToArray();
        string queryAnswer = "[" + string.Join(",", keys.Select(key => $"{{\"key\":\"{key}\"}}")) + "]";
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(
                0,
                RealisticDescriptionGetAnswer(
                    keys.First(key => arguments.Contains(key)), TwgJiraExecutor.Marker(otherTaskId)),
                string.Empty)
            : new ProcessResult(0, queryAnswer, string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> findByMarker = () => executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        TwgExecutionException exception = (await findByMarker.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().Contain(TwgJiraExecutor.Marker(taskId));
    }

    /// <summary>
    /// A dedup hit's own confirmation read can find no readable payload at all — twg's own temp
    /// file for that <c>get</c> reaped or unreadable between the call and this check — and that
    /// must refuse rather than silently read as "no marker", which would create a second card on
    /// an unconfirmed check (independent pre-PR review, adversarial lens, cycle 3; verified fixed,
    /// cycle 4). Modeled the same way <c>A_search_reads_the_key_out_of_twgs_real_envelope_and_file_shape</c>
    /// exercises the real envelope shape, except the temp file the envelope names is never written
    /// at all, standing in for one twg wrote and the system then reaped.
    /// </summary>
    [Fact]
    public async Task A_dedup_hit_whose_own_description_cannot_be_confirmed_read_refuses_rather_than_guesses()
    {
        Guid taskId = Guid.NewGuid();
        string missingFile = Path.Combine(Path.GetTempPath(), $"hall9k-reaped-{Guid.NewGuid():N}.json");
        string envelope =
            $"""
            output_files:
              stdout: "{missingFile}"
              compact: "{missingFile}.compact"
            command: "jira.workitem.get"
            agent_output:
              summary: "stats"
            ---END---
            """;
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(0, envelope, string.Empty)
            : new ProcessResult(0, """[{"key":"PROJ-999"}]""", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> findByMarker = () => executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        TwgExecutionException exception = (await findByMarker.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().Contain("PROJ-999").And.Contain(TwgJiraExecutor.Marker(taskId));
    }

    /// <summary>
    /// The sibling of the defect above, one call earlier: the marker search's own answer — not a
    /// candidate's confirmation read — can be left unreadable by the same reaped-temp-file failure,
    /// and that must refuse rather than fall back to the raw YAML envelope, which fails to parse as
    /// JSON and reads as zero candidates — the affirmative permission to create a duplicate
    /// (independent pre-PR review, conformance lens, cycle 8).
    /// </summary>
    [Fact]
    public async Task A_marker_search_whose_own_answer_cannot_be_confirmed_read_refuses_rather_than_guesses()
    {
        Guid taskId = Guid.NewGuid();
        string missingFile = Path.Combine(Path.GetTempPath(), $"hall9k-reaped-{Guid.NewGuid():N}.json");
        string envelope =
            $"""
            output_files:
              stdout: "{missingFile}"
              compact: "{missingFile}.compact"
            command: "jira.workitem.query"
            agent_output:
              summary: "stats"
            ---END---
            """;
        RecordingProcessRunner twg = RecordingProcessRunner.Succeeding(envelope);
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> findByMarker = () => executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

        TwgExecutionException exception = (await findByMarker.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.Message.Should().Contain(TwgJiraExecutor.Marker(taskId));
    }

    /// <summary>
    /// A third way the marker search's own answer can be unconfirmable, distinct from the two
    /// above: the temp file exists and reads back fine, but what it holds is not valid JSON — a
    /// partially flushed write, or a reaper that emptied it after the <c>File.Exists</c> check but
    /// before the read completed. This used to report <c>confirmedReadable: true</c> alongside the
    /// empty candidate list a JSON parse failure falls back to, so the dedup gate read a garbled
    /// answer as an affirmative "no marker" and would create a duplicate — the identical
    /// false negative <c>confirmedReadable</c> exists to close for an outright unreadable file
    /// (independent pre-PR review, adversarial lens, cycle 5).
    /// </summary>
    [Fact]
    public async Task A_marker_search_whose_own_answer_reads_back_as_malformed_json_refuses_rather_than_guesses()
    {
        Guid taskId = Guid.NewGuid();
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{not valid json");
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

            Func<Task> findByMarker = () => executor.FindByMarkerAsync(taskId, "/repo", CancellationToken.None);

            TwgExecutionException exception = (await findByMarker.Should().ThrowAsync<TwgExecutionException>()).Which;
            exception.Message.Should().Contain(TwgJiraExecutor.Marker(taskId));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_update_writes_the_fields_and_then_verifies_by_reading_back()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(
            null, new Dictionary<string, string> { ["customfield_1"] = "value", ["description"] = "## Heading" }, null);

        TwgWriteResult result = await executor.UpdateAsync("PROJ-123", payload, "/repo", CancellationToken.None);

        result.IssueKey.Should().Be("PROJ-123");
        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) update =
            twg.Calls.Should().Contain(call =>
                call.Arguments.Contains("update") && call.Arguments.Contains("--id") && call.Arguments.Contains("customfield_1=value")).Subject;
        update.Arguments.Should().ContainInOrder("--description-format", "markdown");
        twg.Calls.Should().Contain(call => call.Arguments.Contains("get"));
    }

    [Fact]
    public async Task A_comment_never_transitions_or_closes_the_item()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : "{}", string.Empty));
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
    /// An auth failure raised by the comment's own read-back verification — not the write call
    /// itself — is not reported as the ordinary pending-authentication state: the comment already
    /// landed by that point, and a comment has no dedup gate the way a create's marker search
    /// does, so retrying it once 'twg login' succeeds would post the identical comment a second
    /// time (independent pre-PR review, adversarial lens, cycle 3).
    /// </summary>
    [Fact]
    public async Task An_auth_failure_from_the_comments_own_verification_is_not_reported_as_retryable()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(77, """{"error":{"code":"AUTH_REQUIRED","message":"authentication required"}}""", string.Empty)
            : new ProcessResult(0, "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeFalse();
        exception.Message.Should().Contain("PROJ-123").And.Contain("posted the comment").And.Contain("a second time");
    }

    /// <summary>
    /// A non-auth failure of the comment's own read-back (a transient 5xx, a rate limit, a read
    /// permission problem) must not be recorded as though the comment itself was refused — it
    /// already landed by the time this runs, exactly the class of mistake the auth-specific test
    /// above already covers, extended here to the non-auth case both review lenses independently
    /// found still generic (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_non_auth_failure_from_the_comments_own_verification_is_not_reported_as_a_refusal()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(1, """{"error":{"code":"TWG_COMMAND_FAILED","message":"rate limited"}}""", string.Empty)
            : new ProcessResult(0, "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);

        Func<Task> comment = () => executor.CommentAsync("PROJ-123", "note", "markdown", "/repo", CancellationToken.None);

        TwgExecutionException exception = (await comment.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeFalse();
        exception.Message.Should().Contain("PROJ-123")
            .And.Contain("read-back call")
            .And.Contain("already succeeded")
            .And.Contain("do not record this as a refusal of the write");
    }

    /// <summary>
    /// The identical mistake reported by both review lenses for <c>UpdateAsync</c> and
    /// <c>CreateAsync</c>'s own read-back, not just <c>CommentAsync</c>'s — the same route, the
    /// same wording (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public async Task A_non_auth_failure_from_an_updates_own_verification_is_not_reported_as_a_refusal()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => arguments.Contains("get")
            ? new ProcessResult(1, """{"error":{"code":"TWG_COMMAND_FAILED","message":"rate limited"}}""", string.Empty)
            : new ProcessResult(0, "{}", string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = new(null, new Dictionary<string, string> { ["description"] = "text" }, null);

        Func<Task> update = () => executor.UpdateAsync("PROJ-123", payload, "/repo", CancellationToken.None);

        TwgExecutionException exception = (await update.Should().ThrowAsync<TwgExecutionException>()).Which;
        exception.IsAuthFailure.Should().BeFalse();
        exception.Message.Should().Contain("already succeeded")
            .And.Contain("do not record this as a refusal of the write");
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

    /// <summary>
    /// An envelope's own <c>error.message</c> is twg's and Jira's text, not Hall9k's, and it
    /// routinely quotes a composed field value back — so a permanent, non-auth refusal whose
    /// message happens to contain "unauthorized" (because that word is in the composed summary
    /// this create was refused over) must not classify as an expired login: an auth-classified
    /// write has no retry ceiling, so a misclassification here retries a doomed write forever
    /// (independent pre-PR review, adversarial lens, cycle 11).
    /// </summary>
    [Fact]
    public async Task A_permanent_refusal_that_echoes_an_auth_word_from_the_envelope_is_not_misread_as_one()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.FailingWithEnvelope(
            1,
            "TWG_COMMAND_FAILED",
            "field 'summary' rejected: value 'Return 401 unauthorized instead of 500' exceeds 255 characters");
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
        roundTripped.Fields.Should().ContainKey("customfield_10010").WhoseValue.Should().Be("\"Value\"");
        roundTripped.Comment.Should().Be("A comment");
        roundTripped.ProjectKey.Should().Be("DEV");
    }

    /// <summary>
    /// A select-list option id composed as the JSON string "10501" — the ordinary shape for a
    /// custom field, per the same acceptance criterion (Decisions Log #102) that hands field
    /// composition to an agent in the first place — has to reach twg still carrying its quotes:
    /// twg's own <c>--field</c> parses its argument as JSON when valid, so an unquoted "10501"
    /// is read back as the number 10501 rather than the string it actually is (independent pre-PR
    /// review, cycle 6).
    /// </summary>
    [Fact]
    public async Task A_string_field_that_looks_numeric_reaches_twg_still_quoted()
    {
        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments => new ProcessResult(
            0, arguments.Contains("get") ? RealisticGetAnswer("PROJ-123") : RealisticCreateAnswer, string.Empty));
        TwgJiraExecutor executor = new(twg.Runner);
        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"A card","customfield_10050":"10501"}}""");

        await executor.CreateAsync(Project, payload, Guid.NewGuid(), "/repo", CancellationToken.None);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) create =
            twg.Calls.Should().Contain(call => call.Arguments.Contains("create")).Subject;
        create.Arguments.Should().Contain("customfield_10050=\"10501\"");
    }

    /// <summary>
    /// The same fidelity survives a round trip through <see cref="JiraWritePayload.ToJson"/> and
    /// back — the shape a retry after an expired twg login actually reads (independent pre-PR
    /// review, cycle 6): naive field-value re-serialization would double-escape a quoted string on
    /// exactly this path.
    /// </summary>
    [Fact]
    public void A_quoted_field_value_survives_a_json_round_trip_without_re_escaping()
    {
        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"customfield_10050":"10501"}}""");

        JiraWritePayload roundTripped = JiraWritePayload.FromJson(payload.ToJson());

        roundTripped.Fields.Should().ContainKey("customfield_10050").WhoseValue.Should().Be("\"10501\"");
    }
}
