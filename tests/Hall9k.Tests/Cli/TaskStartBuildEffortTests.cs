using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task start</c> and <c>h9k task delegate</c> write their own settings files from the CLI, where no
/// live <c>DaemonOptions</c> exists, so they resolve the build session's effort over the same chain a
/// dispatcher-launched build gets: task, then project, then the node's build value, then the node-wide value.
/// The node's tiers are read from the durable settings <c>h9k config show</c> renders.
/// </summary>
[Collection("Environment")]
[Trait("Category", "Environment")]
public sealed class TaskStartBuildEffortTests : IDisposable
{
    private static readonly string[] EnvironmentVariables = ["Hall9k__Effort", "Hall9k__EffortByRole__Build"];

    private readonly ScopedTestHome scopedHome = new();

    private readonly Dictionary<string, string?> previous =
        EnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public TaskStartBuildEffortTests()
    {
        foreach (string name in EnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        scopedHome.Dispose();
    }

    [Fact]
    public async Task Nothing_set_at_any_level_resolves_to_unknown_so_the_file_carries_no_effort()
    {
        AgentEffort effort = await TaskStartCommand.ResolveBuildEffortAsync(
            new TaskDetails(), new ProjectDetails(), CancellationToken.None);

        effort.Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public async Task The_node_wide_level_decides_when_nothing_above_it_is_set()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.Effort = "medium", CancellationToken.None);

        (await TaskStartCommand.ResolveBuildEffortAsync(new TaskDetails(), new ProjectDetails(), CancellationToken.None))
            .Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public async Task The_nodes_build_value_wins_over_the_node_wide_level_and_no_other_roles_value_counts()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            s =>
            {
                s.Effort = "medium";
                s.EffortByRole.Build = "xhigh";
                s.EffortByRole.Review = "low";
            },
            CancellationToken.None);

        (await TaskStartCommand.ResolveBuildEffortAsync(new TaskDetails(), new ProjectDetails(), CancellationToken.None))
            .Should().Be(AgentEffort.ExtraHigh);
    }

    [Fact]
    public async Task A_project_value_wins_over_the_nodes_build_value()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.EffortByRole.Build = "xhigh", CancellationToken.None);

        (await TaskStartCommand.ResolveBuildEffortAsync(
            new TaskDetails(), new ProjectDetails { Effort = AgentEffort.Low }, CancellationToken.None))
            .Should().Be(AgentEffort.Low);
    }

    [Fact]
    public async Task A_task_value_wins_over_a_project_value()
    {
        (await TaskStartCommand.ResolveBuildEffortAsync(
            new TaskDetails { Effort = AgentEffort.High }, new ProjectDetails { Effort = AgentEffort.Low },
            CancellationToken.None))
            .Should().Be(AgentEffort.High);
    }

    [Fact]
    public async Task A_build_value_from_the_environment_is_read_the_way_the_daemon_binds_it()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.EffortByRole.Build = "low", CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__EffortByRole__Build", "high");

        (await TaskStartCommand.ResolveBuildEffortAsync(new TaskDetails(), new ProjectDetails(), CancellationToken.None))
            .Should().Be(AgentEffort.High);
    }
}
