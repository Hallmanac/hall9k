using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the board says about a per-project run ceiling (Decisions Log #140). Every assertion here
/// is about a queue state never having to be reconstructed: which limit holds a row, in that
/// limit's own numbers, and — for a project paused at 0 while the machine sits idle — a line
/// nobody can miss.
/// </summary>
public sealed class ProjectCapSurfaceTests
{
    private static readonly Guid ProjectId = DomainId.New();

    [Fact]
    public void A_queued_row_names_its_own_projects_cap_rather_than_the_node_it_is_not_blocked_by()
    {
        // The node has a free slot; the project does not. Naming the node here would send a
        // human to h9k config set, which would not release this row at all.
        TaskStatusRow row = Compose(cap: 1, projectLiveRuns: 1, nodeLiveRuns: 1, nodeCeiling: 2);

        row.Held!.Kind.Should().Be(QueueHoldKind.ProjectCap);
        row.Held.ReasonLine.Should().Be("waiting for a slot — project cap 1 of 1 running");
        row.WaitingForSlot.Should().BeTrue();
    }

    [Fact]
    public void A_project_over_its_cap_reads_as_over_rather_than_as_broken_arithmetic()
    {
        // A cap lowered under live runs, or a resolved review park re-entering one: "3 of 1" on
        // a pane whose job is to say the board is throttled rather than broken.
        TaskStatusRow row = Compose(cap: 1, projectLiveRuns: 3, nodeLiveRuns: 3, nodeCeiling: 5);

        row.Held!.ReasonLine.Should().Be("waiting for a slot — project cap 3 running, over a cap of 1");
    }

    [Fact]
    public void A_paused_project_says_paused_and_names_the_one_lever_that_resumes_it()
    {
        TaskStatusRow row = Compose(cap: 0, projectLiveRuns: 0, nodeLiveRuns: 0, nodeCeiling: 2);

        row.Held!.Kind.Should().Be(QueueHoldKind.ProjectPaused);
        row.Held.ReasonLine.Should().Contain("project 'alpha' is paused at a cap of 0 run(s)");
        row.Held.ReasonLine.Should().Contain("h9k project set alpha --max-parallel-tasks <n>",
            "the pause is only ever released by a human, so the row carries the command");
    }

    [Fact]
    public void The_projects_own_cap_outranks_the_node_ceiling_when_both_are_full()
    {
        // The dispatcher asks the project first (DispatchEngine.ClaimEligibleAsync), so the
        // board must too — otherwise the log and the pane name different causes for one row.
        TaskStatusRow row = Compose(cap: 1, projectLiveRuns: 1, nodeLiveRuns: 2, nodeCeiling: 2);

        row.Held!.Kind.Should().Be(QueueHoldKind.ProjectCap);
    }

    [Fact]
    public void The_node_ceiling_is_named_as_the_node_when_the_projects_cap_still_has_room()
    {
        TaskStatusRow row = Compose(cap: 3, projectLiveRuns: 1, nodeLiveRuns: 2, nodeCeiling: 2);

        row.Held!.Kind.Should().Be(QueueHoldKind.NodeCeiling);
        row.Held.ReasonLine.Should().Be("waiting for a slot — node 2 of 2 running",
            "the two limits print in the same column, so each says which one it is");
    }

    [Fact]
    public void Nothing_is_said_about_slots_when_neither_limit_is_full()
    {
        Compose(cap: 2, projectLiveRuns: 0, nodeLiveRuns: 0, nodeCeiling: 2)
            .Held.Should().BeNull("a queue with room has no measured cause to report");
    }

    [Fact]
    public void An_uncapped_project_still_reads_the_node_ceiling_it_actually_shares()
    {
        // A project the sweep measured no cap for is uncapped, not exempt: the node's own
        // ceiling still holds its rows, and that is what the row says.
        TaskStatusRow row = Compose(cap: null, projectLiveRuns: 2, nodeLiveRuns: 2, nodeCeiling: 2);

        row.Held!.Kind.Should().Be(QueueHoldKind.NodeCeiling);
    }

    [Fact]
    public void A_row_whose_project_the_sweep_never_measured_claims_no_cap()
    {
        // The never-guess rule: the cap is enforced from the dispatcher's own reading, so a
        // project absent from the measurement is one no cap was observed for.
        TaskListItem task = StatusFixtures.Task(TaskState.Queued, projectId: ProjectId);
        DispatchPressure pressure = new(
            LiveRuns: 1, MaxConcurrentRuns: 2,
            Projects: new Dictionary<Guid, ProjectRunCeiling> { [DomainId.New()] = new(1, 1) });

        StatusFixtures.Compose(task, pressure: pressure, projects: Names())
            .Held.Should().BeNull("the node has room and nothing was measured about this project");
    }

    [Fact]
    public void The_paused_line_names_the_project_the_count_it_holds_and_the_lever()
    {
        // The forgotten-cap footgun, answered with visibility rather than automation: two queued
        // tasks held on an idle node, said once for the project rather than once per row.
        IReadOnlyList<TaskStatusRow> rows =
        [
            Compose(cap: 0, projectLiveRuns: 0, nodeLiveRuns: 0, nodeCeiling: 2),
            Compose(cap: 0, projectLiveRuns: 0, nodeLiveRuns: 0, nodeCeiling: 2),
        ];

        IReadOnlyList<string> lines = StatusCommand.PausedProjectLines(
            rows, new DispatchPressure(0, 2, Caps(cap: 0, liveRuns: 0)));

        string line = lines.Should().ContainSingle().Subject;
        line.Should().Contain("PAUSED");
        line.Should().Contain("project 'alpha'");
        line.Should().Contain("2 queued task(s)");
        line.Should().Contain("2 free slot(s)");
        line.Should().Contain("h9k project set alpha --max-parallel-tasks <n>");
    }

    [Fact]
    public void The_paused_line_stays_quiet_when_the_node_has_nothing_free_anyway()
    {
        // Work held on a full node is the node ceiling doing its job, and the queued section
        // already explains that; shouting here would train a reader to ignore the line.
        IReadOnlyList<TaskStatusRow> rows =
            [Compose(cap: 0, projectLiveRuns: 0, nodeLiveRuns: 2, nodeCeiling: 2)];

        StatusCommand.PausedProjectLines(rows, new DispatchPressure(2, 2, Caps(cap: 0, liveRuns: 0)))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_paused_line_stays_quiet_when_no_measurement_is_current()
    {
        IReadOnlyList<TaskStatusRow> rows =
            [Compose(cap: 0, projectLiveRuns: 0, nodeLiveRuns: 0, nodeCeiling: 2)];

        StatusCommand.PausedProjectLines(rows, pressure: null).Should().BeEmpty(
            "a cap nothing has swept against is not yet a cap the dispatcher enforced");
    }

    [Fact]
    public void The_queued_heading_names_every_limit_holding_rows_and_the_lever_for_each()
    {
        string both = StatusCommand.QueuedHeading(
            atCeiling: true, atProjectCap: true, atSpendBudget: false, spend: null);

        both.Should().Contain("the node is at its concurrency ceiling");
        both.Should().Contain("a project is at its own cap, or paused at 0");
        both.Should().Contain("h9k config set --max-concurrent-task-runs <n>");
        both.Should().Contain("h9k project set <project> --max-parallel-tasks <n>");
        both.Should().Contain("no restart", "a project cap lands on the next dispatch cycle, unlike the node's");

        string capOnly = StatusCommand.QueuedHeading(
            atCeiling: false, atProjectCap: true, atSpendBudget: false, spend: null);

        capOnly.Should().NotContain("the node is at its concurrency ceiling",
            "a node with room must never be blamed for a hold its own ceiling is not causing");
    }

    [Fact]
    public void The_queued_heading_promises_a_per_row_limit_only_where_the_rows_carry_one()
    {
        // The spend budget is the node's own single figure with no per-task denominator, so
        // QueueHold never names it and a row it holds carries no limit line. A heading that
        // promised one anyway sent a reader hunting a cause that was never rendered (independent
        // pre-PR review, cycle 1, adversarial lens).
        string mixed = StatusCommand.QueuedHeading(
            atCeiling: false, atProjectCap: true, atSpendBudget: true, spend: Spend());

        mixed.Should().Contain("Each row below held by one of the two counted limits names it");
        mixed.Should().Contain("a row naming none is waiting on the budget");

        string spendOnly = StatusCommand.QueuedHeading(
            atCeiling: false, atProjectCap: false, atSpendBudget: true, spend: Spend());

        spendOnly.Should().Contain("No row below names a limit of its own");
        spendOnly.Should().NotContain("Each row below",
            "not one row in this queue carries a limit line, so nothing here may point a reader at one");
    }

    /// <summary>A budget this node is enforcing and has spent — what the queued section gates on.</summary>
    private static SpendPressure Spend() => new(
        SpentTokens: 500_000,
        BudgetTokens: 500_000,
        ByModel: [],
        NextRollover: new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
        Period: SpendPeriod.Week.Value,
        BudgetIsEnforced: true,
        ConfiguredBudgetTokens: 500_000,
        ConfiguredPeriod: SpendPeriod.Week.Value);

    /// <summary>One queued row of project 'alpha', against a stated node and project measurement.</summary>
    private static TaskStatusRow Compose(int? cap, int projectLiveRuns, int nodeLiveRuns, int nodeCeiling) =>
        StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Queued, projectId: ProjectId),
            pressure: new DispatchPressure(nodeLiveRuns, nodeCeiling, Caps(cap, projectLiveRuns)),
            projects: Names());

    private static Dictionary<Guid, ProjectRunCeiling> Caps(int? cap, int liveRuns) =>
        new() { [ProjectId] = new ProjectRunCeiling(liveRuns, cap) };

    private static Dictionary<Guid, string> Names() => new() { [ProjectId] = "alpha" };
}
