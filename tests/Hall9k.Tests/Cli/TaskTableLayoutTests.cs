using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// One line per task, on every surface that tabulates one. The objective is truncated to the
/// width the fixed columns leave it, and a budget a single character too generous puts every
/// long objective onto a second, nearly empty line — the stacking the truncation exists to
/// prevent. Only the rendering can tell: the check is the rendered line count, not arithmetic
/// that could be wrong in the same way twice.
/// </summary>
public sealed class TaskTableLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Wide enough for these rows' fixed columns and the minimum objective, narrow to roomy.
    /// A console narrower than that is the documented fallback, covered on its own below.
    /// </summary>
    public static TheoryData<int> Widths => [110, 120, 160, 200];

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_table_fits_one_line_per_task(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(TaskListCommand.Rows(rows, scoped: false, width, Now), width);

        // Top border, header, header rule, one line per task, bottom border.
        lines.Should().HaveCount(rows.Count + 4, "a browse row that wraps stacks the list down the page");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_table_fits_one_line_per_task_when_it_is_scoped_to_a_project(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(TaskListCommand.Rows(rows, scoped: true, width, Now), width);

        lines.Should().HaveCount(rows.Count + 4, "dropping the Project column widens the objective, it does not wrap it");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_project_pane_fits_one_line_per_task(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(ProjectShowCommand.TaskTable(rows, width, Now), width);

        lines.Should().HaveCount(rows.Count + 4);
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_attention_pane_fits_one_line_per_task(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = Rows();

        string[] lines = Render(StatusCommand.SectionTable(rows, width, Now), width);

        // Borderless and headerless: nothing but the rows, so a glance takes in the whole section.
        lines.Should().HaveCount(rows.Count, "a pane that scrolls has stopped being glanceable");
    }

    /// <summary>
    /// The attention pane names the assignee (Decisions Log #34) and still fits one line per
    /// task: it is borderless, so it has the width to spend. The browse table deliberately does
    /// not carry the column — six fixed columns already put the objective near its floor there,
    /// and a seventh would wrap every long row to say what one owner already knows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void The_attention_pane_still_fits_one_line_per_task_once_rows_carry_an_assignee(int width)
    {
        IReadOnlyList<TaskStatusRow> rows = [.. Rows().Select(row => row with { Assignee = "Brian Hall" })];

        Render(StatusCommand.SectionTable(rows, width, Now), width).Should().HaveCount(rows.Count);
        string.Join("\n", Render(StatusCommand.SectionTable(rows, width, Now), width))
            .Should().Contain("Brian Hall");
        string.Join("\n", Render(TaskListCommand.Rows(rows, scoped: false, width, Now), width))
            .Should().NotContain("Brian Hall", "the browse table spends its width on the objective");
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

        lines.Should().HaveCountGreaterThan(rows.Count + 4, "the objective wraps once the floor is reached");
        string.Join("\n", lines).Should().Contain("noun-first…",
            "the objective is still cut at the floor, so it is words rather than initials that wrap");
    }

    /// <summary>The rendered table, one string per screen line, at the width it was built for.</summary>
    private static string[] Render(IRenderable table, int width)
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

        console.Write(table);

        return writer.ToString().TrimEnd('\n').Split('\n');
    }

    /// <summary>
    /// A section's worth of rows, carrying what actually stretches a column: a long objective,
    /// the widest status word the composer can produce, and a project name longer than its header.
    /// </summary>
    private static IReadOnlyList<TaskStatusRow> Rows()
    {
        Guid runId = DomainId.New();
        return
        [
            Row(
                Task("Complete the noun-first CLI shape so projects become browsable and inspectable end to end",
                    TaskState.Claimed, runId),
                new RunListItem { Id = runId, State = RunState.Running },
                silentSince: Now.AddHours(-3)),
            Row(Task("Teach the daemon to force-push follow-up branches after an autosquash rebase",
                TaskState.Done, pullRequest: "https://github.com/hallmanac/hall9k/pull/137")),
            Row(Task("Make h9k status narrow to the attention pane and stop being a browse surface",
                TaskState.NeedsHuman)),
            Row(Task("Short one", TaskState.Queued)),
        ];
    }

    private static TaskListItem Task(
        string objective, TaskState state, Guid? runId = null, string? pullRequest = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = objective,
        Type = TaskType.Feature,
        State = state,
        CurrentRunId = runId,
        PullRequestUrl = pullRequest,
        AddedAt = Now.AddDays(-3),
    };

    private static TaskStatusRow Row(
        TaskListItem task, RunListItem? run = null, DateTimeOffset? silentSince = null) =>
        TaskStatusComposer.Compose(
            task,
            run is null ? new Dictionary<Guid, RunListItem>() : new Dictionary<Guid, RunListItem> { [run.Id] = run },
            run is null || silentSince is null
                ? new Dictionary<Guid, RunActivity>()
                : new Dictionary<Guid, RunActivity> { [run.Id] = new() { Id = run.Id, LastActivityAt = silentSince.Value } },
            new Dictionary<Guid, string> { [task.ProjectId] = "hall9k-platform" },
            new Dictionary<Guid, string>(),
            Now);
}
