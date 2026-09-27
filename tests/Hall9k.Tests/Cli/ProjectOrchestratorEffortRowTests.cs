using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k project show</c>'s read-back for the per-project orchestrator effort override (task: the
/// orchestrator window's effort becomes a rendered project and node setting) — the same "before
/// this row existed the only way to see the effective value was to cat a generated file or re-read
/// config.json by hand" reasoning <see cref="ProjectOrchestratorModelRowTests"/> already documents.
/// </summary>
public sealed class ProjectOrchestratorEffortRowTests
{
    [Fact]
    public void The_projects_own_override_wins_and_says_so()
    {
        ProjectDetails project = Project();
        project.OrchestratorEffort = AgentEffort.High;

        string row = ProjectShowCommand.OrchestratorEffortRow(project, new OperatingSettings());

        row.Should().Contain("high");
        row.Should().Contain("this project's own override");
    }

    [Fact]
    public void The_nodes_own_resolution_is_used_absent_any_project_level_setting()
    {
        ProjectDetails project = Project();

        string row = ProjectShowCommand.OrchestratorEffortRow(
            project, new OperatingSettings { OrchestratorEffort = "low" });

        row.Should().Contain("low");
        row.Should().Contain("the node's own resolution");
    }

    [Fact]
    public void Unset_at_every_level_says_so_rather_than_printing_a_bare_value()
    {
        ProjectDetails project = Project();

        string row = ProjectShowCommand.OrchestratorEffortRow(project, new OperatingSettings());

        row.Should().Contain("unset");
        row.Should().Contain("the node's own resolution");
    }

    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "alpha",
    };
}
