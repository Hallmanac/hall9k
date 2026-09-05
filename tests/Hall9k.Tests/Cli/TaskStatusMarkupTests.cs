using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The composed markup every browse and attention surface renders. A row carries values the
/// platform observed rather than values this repo authored, so the markup has to survive
/// whatever those values contain.
/// </summary>
public sealed class TaskStatusMarkupTests
{
    [Theory]
    [InlineData("https://github.com/x/y/pull/7", "#7")]
    [InlineData("https://[::1]:8443/x/y/pull/7", "#7")]
    [InlineData("https://github.com/x/y/pull/7/", "#7")]
    [InlineData("https://github.com/x/y/pull/7?w=1", "#7")]
    public void A_pull_request_url_renders_as_a_link_whatever_markup_characters_it_carries(
        string pullRequest, string label)
    {
        // Spectre reads '[' as the start of a tag, so an unescaped bracket anywhere in the URL
        // throws "malformed markup tag" and takes the whole table down. Escaping is what keeps
        // an observed value from being read as authored markup.
        string markup = Row(pullRequest).PullRequestMarkup;

        string rendered = Render(markup);

        rendered.Should().Contain(pullRequest, "the hyperlink still points where the URL was observed to point");
        rendered.Should().Contain(label, "the column names the pull request the way the phase line does");
    }

    [Theory]
    [InlineData("https://example.com/x/y/pull/[7]")]
    [InlineData("https://github.com/x/y/pull/seven")]
    [InlineData("not-a-url-at-all")]
    public void A_url_carrying_no_readable_number_links_without_claiming_one(string pullRequest)
    {
        // The column and the phase line read the same URL with the same reader
        // (PullRequestUrls.ParseNumber), so neither can name a number the other does not see.
        // Where the reader finds none, the row still shows there is a pull request and stops
        // short of inventing which one — the phase line's "the pull request", one column wide.
        string rendered = Render(Row(pullRequest).PullRequestMarkup);

        rendered.Should().Contain("PR").And.NotContain("#");
        Row(pullRequest).Phase.Text.Should().NotContain("#",
            "the phase line reads the same URL with the same reader and finds no number either");
    }

    [Fact]
    public void A_task_that_never_opened_a_pull_request_renders_nothing_at_all()
    {
        Row(pullRequest: null).PullRequestMarkup.Should().BeEmpty();
        Row(pullRequest: "  ").PullRequestMarkup.Should().BeEmpty("whitespace is not a pull request");
    }

    [Fact]
    public void An_objective_carrying_escape_sequences_cannot_repaint_the_table_it_is_listed_in()
    {
        // Since adoption (PLAN.md §3.1a) an objective can be seeded from an issue title, so this
        // column can be quoting anyone who can file an issue. EscapeMarkup() neutralises Spectre's
        // syntax, not the terminal's: an escape sequence left in the cell clears the screen and
        // repaints it, and a lone CR writes over the row above.
        string objective = "Fix login\u001b[2J\u001b[H\rTask 8a3f: verified, safe to merge";

        string rendered = RenderPlain(Row(pullRequest: null, objective).ObjectiveMarkup(72));

        rendered.Should().NotContain("\u001b").And.NotContain("\r");
        rendered.Should().Contain("Fix login[2J[HTask 8a3f: verified, safe to merge",
            "the characters that were never control characters still read as themselves");
    }

    [Fact]
    public void A_multi_line_objective_stays_on_the_one_line_its_row_is_given()
    {
        string rendered = RenderPlain(
            Row(pullRequest: null, "Adopt issues\nEverything below is approved").ObjectiveMarkup(72));

        rendered.Should().NotContain("\n").And.Contain("Adopt issues Everything below is approved");
    }

    /// <summary>
    /// The lever is a composed h9k command on most rows, but where the platform has no command
    /// to offer it is the pull request's own URL — and h9k task resolve --pr stores that string
    /// exactly as it was typed, with no validation. So the lever is outside text on the same
    /// terms as the cause beside it. Origin incident (pre-PR review cycle 4, 2026-08-22): the
    /// cause went through ExternalText and the lever was escaped for markup only, which leaves
    /// the terminal's own control characters intact.
    /// </summary>
    [Fact]
    public void A_lever_that_is_a_pull_request_url_cannot_repaint_the_pane_it_is_printed_in()
    {
        string url = "https://github.com/x/y/pull/9\u001b[2J\u001b[H\rnothing needs you";
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, pullRequest: url));

        row.Attention.Lever.Should().Be(url, "the record is carried as it was observed");

        string rendered = RenderPlain(row.Attention.Markup);

        rendered.Should().NotContain("\u001b").And.NotContain("\r");
        rendered.Should().Contain("https://github.com/x/y/pull/9[2J[Hnothing needs you",
            "the characters that were never control characters still read as themselves");
    }

    /// <summary>
    /// A pr-review task's Done never watched a merge (Decisions Log #99): its own park resolves
    /// with <c>h9k review resolve --merge-ready</c> directly, with no pull request of its own to
    /// open. The ledger's closeout-reason prose has to say that rather than the merge-observed
    /// wording every merge-watching task type earns, or it asserts an observation nobody made —
    /// exactly the Windows field report item 12 defect (2026-09-03): pr-review task 7f1812db
    /// reported "the merge was observed" while the pull request it reviewed still sat open.
    /// </summary>
    [Theory]
    [InlineData("Feature")]
    [InlineData("Bugfix")]
    [InlineData("Refactor")]
    [InlineData("Chore")]
    [InlineData("Research")]
    public void A_merge_watching_task_types_done_reason_names_the_observed_merge(string taskType)
    {
        TaskShowCommand.DoneReason(taskType, "https://github.com/x/y/pull/1")
            .Should().Be("the merge was observed");
    }

    /// <summary>
    /// A merge-watching task closed by hand with no pull request ever opened never had a merge
    /// to watch (independent pre-PR review, cycle 1, conformance and adversarial lenses): the
    /// same never-guess-at-unobserved-facts rule this branch fixes for a pr-review task applies
    /// here too, since asserting "the merge was observed" for a task that never pushed is the
    /// identical unobserved-fact claim under a different task type.
    /// </summary>
    [Theory]
    [InlineData("Feature")]
    [InlineData("Bugfix")]
    [InlineData("Refactor")]
    [InlineData("Chore")]
    [InlineData("Research")]
    public void A_merge_watching_task_type_closed_with_no_pull_request_names_nothing_to_watch(string taskType)
    {
        TaskShowCommand.DoneReason(taskType, "")
            .Should().Be("the task was closed with no pull request to watch");
    }

    [Fact]
    public void A_pr_review_tasks_done_reason_names_the_delivered_review_never_a_merge()
    {
        // A pr-review task's own PullRequestUrl names the pull request it reviewed, not one of
        // its own (PrReviewEngine.FinalizeAsync) — a non-blank URL here must not flip the answer
        // to the merge-observed wording the way it would for a merge-watching task type.
        string reason = TaskShowCommand.DoneReason(
            TaskType.PrReview, "https://github.com/AgelessRx/arx-platform/pull/1976");

        reason.Should().Be("the review was delivered");
        reason.Should().NotContain("merge", "no merge was ever watched for a pr-review task's own closeout");
    }

    [Fact]
    public void A_done_pr_review_row_never_renders_the_merge_was_observed()
    {
        // Windows field report item 12 (2026-09-03): PrReviewEngine.FinalizeAsync records the
        // reviewed pull request's own URL on TaskCompleted and completes the run alongside it —
        // reaching lifecycle Done exactly as an ordinary merged task does — even though that
        // pull request (AgelessRx/arx-platform#1976) sat open the whole time. Nothing here ever
        // watched it merge, so the reason must not claim otherwise.
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(
            TaskState.Done, runId, "https://github.com/AgelessRx/arx-platform/pull/1976", type: TaskType.PrReview);
        RunDetails run = StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null);

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        row.State.Should().Be(LifecycleState.Done, "PrReviewEngine.FinalizeAsync completes the run the same way a merge would");

        string gloss = TaskShowCommand.StateGloss(row);

        gloss.Should().Contain("the review was delivered")
            .And.NotContain("the merge was observed");
    }

    [Fact]
    public void A_done_feature_row_that_pushed_and_merged_still_asserts_the_observed_merge()
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(
            TaskState.Done, runId, "https://github.com/x/y/pull/1", type: TaskType.Feature);
        RunDetails run = StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null);

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        TaskShowCommand.StateGloss(row).Should().Contain("the merge was observed");
    }

    /// <summary>
    /// A Feature task closed by hand with no pull request ever opened (<c>h9k task resolve</c>
    /// with no <c>--pr</c>) never had a merge to watch: <see cref="TaskStatusComposer.Closed"/>
    /// still reads it as true closeout for display purposes (there was nothing to observe), but
    /// the reason has to say so honestly rather than claiming a merge nobody watched.
    /// </summary>
    [Fact]
    public void A_done_feature_row_with_no_pull_request_names_nothing_to_watch()
    {
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, type: TaskType.Feature));

        row.State.Should().Be(LifecycleState.Done, "nothing was ever pushed, so there is no merge to wait for");

        TaskShowCommand.StateGloss(row).Should()
            .Contain("the task was closed with no pull request to watch")
            .And.NotContain("the merge was observed");
    }

    /// <summary>
    /// A console that adds no escape sequences of its own, so the only ones a rendered cell could
    /// carry are the ones the value smuggled in.
    /// </summary>
    private static string RenderPlain(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            // Spectre's default profile enrichers turn ANSI back on whenever they recognise the
            // host CI (GitHub Actions among them), whatever AnsiSupport.No asked for. Left on,
            // the styling Spectre itself emits would put escape sequences in the rendered string
            // and these assertions would be reading the harness rather than the value.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;

        console.Markup(markup);

        return writer.ToString();
    }

    private static string Render(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = 200;

        console.Markup(markup);

        return writer.ToString();
    }

    private static TaskStatusRow Row(string? pullRequest, string objective = "x") =>
        StatusFixtures.Compose(
            StatusFixtures.Task(
                TaskState.Done, pullRequest: pullRequest, objective: objective));
}
