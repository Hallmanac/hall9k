using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k project show</c>'s read-back for the per-project orchestrator model override (task: an
/// operator starts a lean node or project orchestrator window) — before this row existed, the
/// only way to see the effective value was to cat the project's own recipes/settings.json or
/// re-read config.json by hand (independent pre-PR review, cycle 3, conformance lens).
/// </summary>
public sealed class ProjectOrchestratorModelRowTests
{
    [Fact]
    public void The_projects_own_override_wins_and_says_so()
    {
        ProjectDetails project = Project();
        project.OrchestratorModel = "opus";

        string row = ProjectShowCommand.OrchestratorModelRow(project, new OperatingSettings());

        row.Should().Contain("opus");
        row.Should().Contain("this project's own override");
    }

    [Fact]
    public void The_projects_agent_dispatch_model_is_used_absent_an_override()
    {
        ProjectDetails project = Project();
        project.Model = "sonnet";

        string row = ProjectShowCommand.OrchestratorModelRow(project, new OperatingSettings());

        row.Should().Contain("sonnet");
        row.Should().Contain("agent-dispatch model");
    }

    [Fact]
    public void The_nodes_own_resolution_is_used_absent_any_project_level_setting()
    {
        ProjectDetails project = Project();

        string row = ProjectShowCommand.OrchestratorModelRow(project, new OperatingSettings { OrchestratorModel = "haiku" });

        row.Should().Contain("haiku");
        row.Should().Contain("the node's own resolution");
    }

    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "alpha",
    };
}
