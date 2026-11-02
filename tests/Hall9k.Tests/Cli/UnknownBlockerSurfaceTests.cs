using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A node that has no record of a blocker records nothing about it (the dependency sweep), so the
/// surfaces that report the task have to say it does not know. The board and the feed read the
/// same <see cref="UnknownBlocker"/> sentence; <c>h9k task show</c> carries it in its "Waiting on" row.
/// </summary>
public sealed class UnknownBlockerSurfaceTests
{
    private static readonly Guid Blocker = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");

    [Fact]
    public void A_blocked_row_says_the_blocker_is_unknown_beside_its_blocked_by_line()
    {
        TaskListItem blocked = StatusFixtures.Task(TaskState.Blocked);
        blocked.UnmetDependencies = [Blocker];

        TaskStatusRow row = StatusFixtures.Compose(blocked, unknownBlockerIds: [Blocker]);

        row.Facts.Should().Equal(
            "waiting on 1 dependency to close out",
            "blocked by 37b5ec69",
            "unknown blocker 37b5ec69: it most likely belongs to a project in a solution held on a "
            + "teammate's node, so ask that teammate about it");
        row.Attention.NeedsYou.Should().BeFalse("the sweep recorded nothing, so nothing is claimed dead");
    }

    [Fact]
    public void A_blocked_row_whose_blockers_are_all_known_says_nothing_extra()
    {
        TaskListItem blocked = StatusFixtures.Task(TaskState.Blocked);
        blocked.UnmetDependencies = [Blocker];

        StatusFixtures.Compose(blocked).Facts.Should().NotContain(fact => fact.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void The_feed_heading_names_the_unknown_blocker_by_short_id_and_never_by_objective()
    {
        TaskDetails task = new() { Id = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5aaaa"), Objective = "ship the thing" };

        OrchestratorFeedPrinter.Heading(task, [Blocker]).Should().Be(
            "37b5aaaa  ship the thing  (unknown blocker 37b5ec69: it most likely belongs to a project in a "
            + "solution held on a teammate's node, so ask that teammate about it)");
        OrchestratorFeedPrinter.Heading(task, []).Should().Be("37b5aaaa  ship the thing");
    }
}
