using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k project show</c> says about the dispatch tier (Decisions Log #141). The two levers
/// have to read as different things wherever they are reported — a tier releases itself, a cap of
/// 0 does not — which is why the row says so in its own words rather than printing a bare word,
/// the discipline <see cref="ProjectCapCommandSurfaceTests"/> already holds the cap's row to.
/// </summary>
public sealed class ProjectPriorityCommandSurfaceTests
{
    [Fact]
    public void The_default_tier_reads_as_the_rotation_rather_than_as_no_scheduling()
    {
        string row = ProjectShowCommand.PriorityRow(Project(ProjectPriority.Normal));

        row.Should().Contain("normal — the default");
        row.Should().Contain("whichever eligible project has gone longest without a dispatch takes the next one");
        row.Should().Contain("h9k project set alpha --priority high");
    }

    [Fact]
    public void A_focused_project_says_it_releases_itself_and_that_nothing_preempts()
    {
        // The two facts that decide whether a tier or a pause is the right lever, and the one
        // that decides whether an operator expects a running agent to be killed.
        string row = ProjectShowCommand.PriorityRow(Project(ProjectPriority.High));

        row.Should().Contain("high — focus");
        row.Should().Contain("releases itself the moment its queue drains");
        row.Should().Contain("unlike a pause");
        row.Should().Contain("runs already live finish regardless");
    }

    [Fact]
    public void A_background_project_says_a_queue_elsewhere_can_hold_it_indefinitely()
    {
        string row = ProjectShowCommand.PriorityRow(Project(ProjectPriority.Low));

        row.Should().Contain("low — background");
        row.Should().Contain("can hold it indefinitely", "the cost of the tier is stated where it is read");
        row.Should().Contain("h9k project set alpha --priority normal");
    }

    [Fact]
    public void A_tier_this_build_does_not_recognize_is_reported_as_unrecognized_rather_than_as_normal()
    {
        // It schedules as normal — Unknown shares the default's rank — but reporting it as normal
        // would put a choice nobody made in front of the operator (AGENTS.md: never guess at
        // unobserved facts).
        string row = ProjectShowCommand.PriorityRow(Project(ProjectPriority.FromInput("urgent")));

        row.Should().Contain("Unknown — unrecognized");
        row.Should().Contain("scheduled as normal rather than guessed at");
    }

    private static ProjectDetails Project(ProjectPriority priority) => new()
    {
        Id = DomainId.New(),
        Name = "alpha",
        Priority = priority,
    };
}
