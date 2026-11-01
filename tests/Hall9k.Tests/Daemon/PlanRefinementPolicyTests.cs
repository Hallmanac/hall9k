using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>The refinement role's chain (project, then the node's role value, then the node's default) and the three daemon options a plan reads.</summary>
public sealed class PlanRefinementPolicyTests
{
    [Fact]
    public void The_model_is_the_projects_then_the_nodes_refinement_value_then_the_nodes_default_and_a_drafts_own_model_does_not_move_it()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-sonnet-5-5",
            ModelByRole = new RoleModelDefaults { Refinement = "claude-opus-5-5[1m]" },
        };

        options.ResolveModel(AgentRole.Refinement, null, AgentModel.Haiku).Should().Be(AgentModel.Haiku);
        options.ResolveModel(AgentRole.Refinement, null, null).Value.Should().Be("claude-opus-5-5[1m]");
        new DaemonOptions { DefaultModel = "claude-sonnet-5-5" }.ResolveModel(AgentRole.Refinement, null, null).Value
            .Should().Be("claude-sonnet-5-5");

        // The engine passes a null task override: a build model on the draft is not the refinement's.
        options.ResolveModel(AgentRole.Refinement, taskModel: null, projectModel: null)
            .Should().NotBe(options.ResolveModel(AgentRole.Refinement, AgentModel.Haiku, null));
    }

    [Fact]
    public void The_effort_follows_the_same_chain_through_the_projects_the_refinement_value_and_the_node_wide_value()
    {
        DaemonOptions options = new() { Effort = "low", EffortByRole = new RoleEffortDefaults { Refinement = "xhigh" } };

        options.ResolveEffort(AgentRole.Refinement, null, AgentEffort.Medium).Should().Be(AgentEffort.Medium);
        options.ResolveEffort(AgentRole.Refinement, null, null).Should().Be(AgentEffort.ExtraHigh);
        new DaemonOptions { Effort = "low" }.ResolveEffort(AgentRole.Refinement, null, null).Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void The_options_default_to_a_thirty_minute_bound_two_rounds_and_a_five_minute_child_bound()
    {
        DaemonOptions options = new();

        options.PlanRefinementTimeout.Should().Be(TimeSpan.FromMinutes(30));
        options.PlanRoundCap.Should().Be(2);
        options.PlanCommandTimeout.Should().Be(TimeSpan.FromMinutes(5));
        options.PlanPollInterval.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }
}
