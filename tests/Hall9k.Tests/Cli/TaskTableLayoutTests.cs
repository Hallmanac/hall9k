using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// One line per task plus its summary line on the browse surfaces, and one line plus every detail
/// line on the attention pane. The objective is truncated to the width the fixed columns leave it,
/// and a budget a
/// single character too generous puts every long objective onto a second, nearly empty line — the
/// stacking the truncation exists to prevent. Only the rendering can tell: the check is the
/// rendered line count, not arithmetic that could be wrong in the same way twice.
/// </summary>
public sealed class TaskTableLayoutTests
{
    private static readonly DateTimeOffset Now = StatusFixtures.Now;

    /// <summary>
    /// Wide enough for these rows' fixed columns and the minimum objective, narrow to roomy.
    /// A console narrower than that is the documented fallback, covered on its own below.
    /// </summary>
    public static TheoryData<int> Widths => [110, 120, 160, 200];

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_list_fits_one_line_per_task_and_one_summary_line_under_it(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(TaskListCommand.Rows(rows, scoped: false, width, Now), width);

        lines.Should().HaveCount(Expected(rows), "a browse row that wraps stacks the list down the page");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_list_fits_when_it_is_scoped_to_a_project(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(TaskListCommand.Rows(rows, scoped: true, width, Now), width);

        lines.Should().HaveCount(Expected(rows), "dropping the Project column widens the objective, it does not wrap it");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_project_pane_fits_one_line_per_task_and_one_summary_line_under_it(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(ProjectShowCommand.TaskTable(rows, width, Now), width);

        lines.Should().HaveCount(Expected(rows));
    }

    /// <summary>
    /// The browse surfaces kept the distinctions the Status column stopped carrying. Origin
    /// incident (pre-PR review, 2026-08-22): the lifecycle column landed on h9k task list and
    /// h9k project show without the lines that replaced the run vocabulary, so three published
    /// tasks — one unassigned, one queued, one blocked — read as three identical Published rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void A_browse_row_says_which_kind_of_published_or_working_row_it_is(int width)
    {
        Guid projectId = DomainId.New();
        Guid runId = DomainId.New();
        IReadOnlyList<TaskStatusRow> published =
        [
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Published, projectId: projectId)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued, projectId: projectId)),
            StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId, projectId: projectId),
                StatusFixtures.Run(runId, RunState.Running)),
        ];

        string list = string.Join("\n", Render(TaskListCommand.Rows(published, scoped: true, width, Now), width));

        list.Should().Contain("not assigned").And.Contain("the dispatcher has not claimed it yet");
        list.Should().Contain("building", "the phase line is what a Working row is distinguished by");
        // The run vocabulary is the summary line's material, never the Status column's.
        list.Should().NotContain("Queued").And.NotContain("Running");
    }

    /// <summary>
    /// A browse row is at most two lines: the row, and the one summary line it can afford. The
    /// attention pane prints every detail line a row has; a list of twenty tasks doing the same
    /// would stop being a list.
    /// </summary>
    [Fact]
    public void A_browse_row_carries_one_summary_line_where_the_pane_carries_them_all()
    {
        Guid runId = DomainId.New();
        TaskStatusRow needsHuman = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.NeedsHuman, runId), StatusFixtures.Run(runId, RunState.Running));

        needsHuman.DetailMarkup.Should().HaveCount(2, "the phase and the ask are both worth a line on the pane");
        needsHuman.SummaryMarkup.Should().ContainSingle().Which
            .Should().Be(needsHuman.DetailMarkup[0], "the browse line is the most specific one");
    }

    /// <summary>
    /// The attention pane is a row plus its detail lines (Decisions Log #66), and every one of
    /// those is exactly one screen line: a phase or an attention cause that wrapped would undo
    /// the glanceability the whole pane is for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void The_attention_pane_gives_every_row_and_every_detail_line_one_line_each(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();
        int expected = rows.Count + rows.Sum(row => row.DetailMarkup.Count);

        string[] lines = Render(StatusCommand.SectionRows(rows, width, Now), width);

        lines.Should().HaveCount(expected, "a pane that scrolls has stopped being glanceable");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_attention_pane_shows_the_lifecycle_word_the_phase_and_the_ask(int width)
    {
        string pane = string.Join("\n", Render(StatusCommand.SectionRows(Rows(), width, Now), width));

        pane.Should().Contain("Working").And.Contain("Delivered").And.Contain("Published");
        pane.Should().Contain("building", "the phase line says what the machinery is doing");
        pane.Should().Contain("needs you", "the attention column is a first-class column");
        // The run vocabulary is the phase line's material and never the Status column's.
        pane.Should().NotContain("AwaitingReview").And.NotContain("ClosingOut");
    }

    /// <summary>
    /// The attention pane names the assignee (Decisions Log #34) and still fits one line per
    /// row: it has the width to spend. The browse list deliberately does not carry the column —
    /// seven fixed columns already put the objective near its floor there, and an eighth would
    /// wrap every long row to say what one owner already knows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void The_attention_pane_still_fits_once_rows_carry_an_assignee(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = [.. Rows().Select(row => row with { Assignee = "Brian Hall" })];
        int expected = rows.Count + rows.Sum(row => row.DetailMarkup.Count);

        Render(StatusCommand.SectionRows(rows, width, Now), width).Should().HaveCount(expected);
        string.Join("\n", Render(StatusCommand.SectionRows(rows, width, Now), width))
            .Should().Contain("Brian Hall");
        string.Join("\n", Render(TaskListCommand.Rows(rows, scoped: false, width, Now), width))
            .Should().NotContain("Brian Hall", "the browse list spends its width on the objective");
    }

    [Fact]
    public void A_wider_console_shows_more_of_the_objective_rather_than_the_same_truncation()
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string narrow = string.Join("\n", Render(TaskListCommand.Rows(rows, scoped: false, 110, Now), 110));
        string wide = string.Join("\n", Render(TaskListCommand.Rows(rows, scoped: false, 160, Now), 160));

        narrow.Should().Contain("Complete the noun-first CLI");
        wide.Should().Contain("Complete the noun-first CLI shape so projects become browsable");
        narrow.Should().NotContain("projects become browsable", "a narrow console pays for the fixed columns first");
    }

    [Fact]
    public void A_console_too_narrow_for_a_readable_objective_wraps_rather_than_shrinking_to_noise()
    {
        // The floor is deliberate: an objective cut to a word and an ellipsis says nothing, so
        // a console that cannot pay for the fixed columns and a readable objective gets a
        // wrapped row instead. Above the floor the one-line promise holds, which is the theory
        // above; this is the honest fallback below it.
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(TaskListCommand.Rows(rows, scoped: false, 90, Now), 90);

        lines.Should().HaveCountGreaterThan(Expected(rows), "the objective wraps once the floor is reached");
        string.Join("\n", lines).Should().Contain("noun-first…",
            "the objective is still cut at the floor, so it is words rather than initials that wrap");
    }

    /// <summary>
    /// What a browse surface costs in screen lines: its header, one line per row, and each row's
    /// summary line. Anything more than that is a row that wrapped.
    /// </summary>
    private static int Expected(IReadOnlyList<TaskStatusRow> rows) =>
        1 + rows.Count + rows.Sum(row => row.SummaryMarkup.Count);

    /// <summary>The rendered surface, one string per screen line, at the width it was built for.</summary>
    private static string[] Render(IRenderable renderable, int width)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;

        console.Write(renderable);

        return writer.ToString().TrimEnd('\n').Split('\n');
    }

    /// <summary>
    /// A section's worth of rows, carrying what actually stretches a column: a long objective,
    /// the widest lifecycle word the composer can produce, a project name longer than its header,
    /// and one row of each kind of detail line.
    /// </summary>
    private static IReadOnlyList<TaskStatusRow> Rows()
    {
        Guid projectId = DomainId.New();
        Guid buildingRunId = DomainId.New();
        Guid deliveredRunId = DomainId.New();
        Dictionary<Guid, string> projects = new() { [projectId] = "hall9k-platform" };

        return
        [
            StatusFixtures.Compose(
                StatusFixtures.Task(
                    TaskState.Claimed, buildingRunId,
                    objective: "Complete the noun-first CLI shape so projects become browsable and inspectable end to end",
                    projectId: projectId),
                StatusFixtures.Run(buildingRunId, RunState.Running),
                silentSince: Now.AddMinutes(-3),
                projects: projects),
            StatusFixtures.Compose(
                StatusFixtures.Task(
                    TaskState.Done, deliveredRunId, "https://github.com/hallmanac/hall9k/pull/137",
                    objective: "Teach the daemon to force-push follow-up branches after an autosquash rebase",
                    projectId: projectId),
                StatusFixtures.Run(deliveredRunId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 137),
                projects: projects),
            StatusFixtures.Compose(
                StatusFixtures.Task(
                    TaskState.NeedsHuman,
                    objective: "Make h9k status narrow to the attention pane and stop being a browse surface",
                    projectId: projectId),
                projects: projects),
            StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Queued, objective: "Short one", projectId: projectId),
                projects: projects),
        ];
    }
}
