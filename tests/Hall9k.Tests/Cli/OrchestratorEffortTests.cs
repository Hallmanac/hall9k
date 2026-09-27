using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The reasoning effort an orchestrator recipe's settings.json is rendered for (task: the
/// orchestrator window's effort becomes a rendered project and node setting) is its own chain,
/// independent of the agent-dispatch one: an orchestrator-specific override outranks everything
/// else, and no dispatch effort — a task's own, a project's, the node's per-role value, or the
/// node-wide value — ever feeds it, the same independence <see cref="OrchestratorModelTests"/>
/// already covers for the model.
/// </summary>
public sealed class OrchestratorEffortTests
{
    [Fact]
    public void ForNode_falls_through_to_unknown_when_nothing_is_configured()
    {
        OrchestratorEffort.ForNode(new OperatingSettings()).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void ForNode_prefers_its_own_override()
    {
        OperatingSettings settings = new() { OrchestratorEffort = "high" };

        OrchestratorEffort.ForNode(settings).Should().Be(AgentEffort.High);
    }

    [Fact]
    public void ForNode_treats_a_hand_edited_default_literal_as_unset()
    {
        OperatingSettings settings = new() { OrchestratorEffort = "default" };

        OrchestratorEffort.ForNode(settings).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void ForNode_ignores_the_node_wide_dispatch_effort()
    {
        // The node-wide --effort (DaemonOptions.Effort) is a dispatch-chain setting and must never
        // feed the window's own chain — the exact regression the challenge verdict named: a node
        // running a high node-wide --effort must not have its window silently raised the first
        // time this setting re-renders (journal.md, must-change item 2).
        OperatingSettings settings = new() { Effort = "high" };

        OrchestratorEffort.ForNode(settings).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void ForNode_ignores_the_node_wide_per_role_effort()
    {
        OperatingSettings settings = new() { EffortByRole = new RoleEffortSettings { Build = "xhigh" } };

        OrchestratorEffort.ForNode(settings).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void ForProject_falls_through_to_the_node_when_the_project_sets_no_override()
    {
        OperatingSettings settings = new() { OrchestratorEffort = "medium" };

        OrchestratorEffort.ForProject(AgentEffort.Unknown, settings).Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public void ForProject_prefers_its_own_override_over_the_node()
    {
        OperatingSettings settings = new() { OrchestratorEffort = "medium" };

        OrchestratorEffort.ForProject(AgentEffort.Low, settings).Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void ForProject_ignores_the_projects_own_dispatch_effort()
    {
        // Unlike OrchestratorModel.ForProject, there is no "fall through to the project's own
        // dispatch value" rung here — this chain has no legacy behavior predating the override to
        // preserve (journal.md item 2's asymmetry note). A project's ordinary --effort (its
        // dispatch-chain value) is not even a parameter ForProject accepts, so it structurally
        // cannot leak into the window's own resolution.
        OperatingSettings settings = new();

        OrchestratorEffort.ForProject(AgentEffort.Unknown, settings).Should().Be(AgentEffort.Unknown);
    }
}
