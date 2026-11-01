using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A draft challenge runs on Opus 5.5 at high effort on any node, whatever that node's default model, node-wide effort,
/// or settings for other roles say. The rule lives in the model policy and nowhere else, so these are its whole contract.
/// </summary>
public sealed class ChallengeModelPolicyTests
{
    [Fact]
    public void With_nothing_configured_a_challenge_runs_on_opus_five_five_at_high_effort()
    {
        DaemonOptions options = new();

        options.ResolveChallengeModel(projectModel: null).Value.Should().Be("claude-opus-5-5[1m]")
            .And.Be(AgentModel.ChallengeDefault);
        options.ResolveChallengeEffort(projectEffort: null).Should().Be(AgentEffort.High);
    }

    [Fact]
    public void A_cheaper_node_default_model_node_wide_effort_and_other_roles_do_not_move_it()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-sonnet-5-5",
            Effort = "low",
            ModelByRole = new RoleModelDefaults { Refinement = "haiku", Review = "haiku", Publication = "haiku", Courier = "haiku" },
            EffortByRole = new RoleEffortDefaults { Refinement = "low", Review = "low", Courier = "low" },
        };

        options.ResolveChallengeModel(projectModel: null).Value.Should().Be(AgentModel.ChallengeDefault);
        options.ResolveChallengeEffort(projectEffort: null).Should().Be(AgentEffort.High);
    }

    [Fact]
    public void The_node_roles_change_it_for_the_node()
    {
        DaemonOptions options = new()
        {
            ModelByRole = new RoleModelDefaults { Challenge = "claude-opus-5-5" },
            EffortByRole = new RoleEffortDefaults { Challenge = "xhigh" },
        };

        options.ResolveChallengeModel(projectModel: null).Value.Should().Be("claude-opus-5-5");
        options.ResolveChallengeEffort(projectEffort: null).Should().Be(AgentEffort.ExtraHigh);
    }

    [Fact]
    public void A_projects_own_model_and_effort_still_win_as_they_do_for_the_courier()
    {
        DaemonOptions options = new()
        {
            ModelByRole = new RoleModelDefaults { Challenge = "claude-opus-5-5" },
            EffortByRole = new RoleEffortDefaults { Challenge = "xhigh" },
        };

        options.ResolveChallengeModel(projectModel: AgentModel.Sonnet).Should().Be(AgentModel.Sonnet);
        options.ResolveChallengeEffort(projectEffort: AgentEffort.Low).Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void The_roles_ship_blank_so_the_floor_is_not_a_second_shipped_opinion()
    {
        new RoleModelDefaults().For(AgentRole.Challenge).Should().Be(AgentModel.Unknown);
        new RoleEffortDefaults().For(AgentRole.Challenge).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void The_bound_defaults_to_thirty_minutes()
    {
        new DaemonOptions().ChallengeTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }
}
