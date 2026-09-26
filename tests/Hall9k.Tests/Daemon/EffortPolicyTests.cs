using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The effort chain as <see cref="DaemonOptions"/> resolves it at a dispatch site: task, then project, then
/// the node's value for the role, then the node-wide value, then nothing, which leaves the model's own
/// default in force. It is the reverse of the model chain in one respect, which these tests pin: the
/// project's value sits above the node's role value.
/// </summary>
public sealed class EffortPolicyTests
{
    [Fact]
    public void Nothing_set_anywhere_resolves_to_unknown_for_every_role()
    {
        DaemonOptions options = new();

        foreach (AgentRole role in new[]
                 {
                     AgentRole.Build, AgentRole.Review, AgentRole.Fix, AgentRole.Synthesis, AgentRole.Refinement,
                     AgentRole.Publication, AgentRole.Courier,
                 })
        {
            options.ResolveEffort(role, taskEffort: null, projectEffort: null).Should().Be(
                AgentEffort.Unknown, $"{role.Value} has no level at any tier, so its settings file carries none");
        }
    }

    [Fact]
    public void The_node_wide_effort_decides_when_the_role_sets_none()
    {
        DaemonOptions options = new() { Effort = "medium" };

        options.ResolveEffort(AgentRole.Build, null, null).Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public void A_role_value_wins_over_the_node_wide_value_and_only_for_that_role()
    {
        DaemonOptions options = new()
        {
            Effort = "medium",
            EffortByRole = new RoleEffortDefaults { Build = "xhigh" },
        };

        options.ResolveEffort(AgentRole.Build, null, null).Should().Be(AgentEffort.ExtraHigh);
        options.ResolveEffort(AgentRole.Fix, null, null).Should().Be(
            AgentEffort.Medium, "a role with no value of its own falls through to the node-wide level");
    }

    [Fact]
    public void A_role_with_no_value_and_no_node_wide_value_falls_through_to_unset()
    {
        DaemonOptions options = new() { EffortByRole = new RoleEffortDefaults { Build = "xhigh" } };

        options.ResolveEffort(AgentRole.Review, null, null).Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void A_project_value_wins_over_the_nodes_role_value()
    {
        DaemonOptions options = new()
        {
            Effort = "low",
            EffortByRole = new RoleEffortDefaults { Build = "xhigh" },
        };

        options.ResolveEffort(AgentRole.Build, taskEffort: null, projectEffort: AgentEffort.Medium)
            .Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public void A_task_value_wins_over_a_project_value()
    {
        DaemonOptions options = new() { EffortByRole = new RoleEffortDefaults { Build = "xhigh" } };

        options.ResolveEffort(AgentRole.Build, taskEffort: AgentEffort.Low, projectEffort: AgentEffort.High)
            .Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void A_role_with_an_unrecognized_value_is_treated_as_unset_and_never_reaches_a_session()
    {
        DaemonOptions options = new()
        {
            Effort = "high",
            EffortByRole = new RoleEffortDefaults { Build = "max" },
        };

        options.ResolveEffort(AgentRole.Build, null, null).Should().Be(
            AgentEffort.High, "the four accepted names are a closed set, and 'max' is session-only");
    }

    [Fact]
    public void The_interactive_role_has_no_level_of_its_own()
    {
        RoleEffortDefaults roles = new()
        {
            Build = "xhigh", Review = "xhigh", Fix = "xhigh", Synthesis = "xhigh", Refinement = "xhigh",
            Publication = "xhigh", Courier = "xhigh",
        };

        roles.For(AgentRole.Interactive).Should().Be(
            AgentEffort.Unknown, "a person's own session honors their user-level setting, not a node value");
    }

    [Fact]
    public void A_verify_pass_falls_through_to_review_before_anything_beneath_the_role()
    {
        DaemonOptions options = new()
        {
            Effort = "low",
            EffortByRole = new RoleEffortDefaults { Review = "high" },
        };

        options.ResolveVerifyReviewEffort(null, null).Should().Be(AgentEffort.High);
    }

    [Fact]
    public void A_verify_pass_value_wins_over_reviews_own()
    {
        DaemonOptions options = new()
        {
            EffortByRole = new RoleEffortDefaults { Review = "high", ReviewVerify = "low" },
        };

        options.ResolveVerifyReviewEffort(null, null).Should().Be(AgentEffort.Low);
        options.ResolveEffort(AgentRole.Review, null, null).Should().Be(
            AgentEffort.High, "the pass knob narrows one pass shape and never moves the ordinary review chain");
    }

    [Fact]
    public void A_final_pass_falls_through_to_review_and_is_independent_of_the_verify_knob()
    {
        DaemonOptions options = new()
        {
            EffortByRole = new RoleEffortDefaults { Review = "high", ReviewVerify = "low" },
        };

        options.ResolveFinalFullPassReviewEffort(null, null).Should().Be(AgentEffort.High);

        options.EffortByRole.ReviewFinalFullPass = "xhigh";
        options.ResolveFinalFullPassReviewEffort(null, null).Should().Be(AgentEffort.ExtraHigh);
        options.ResolveVerifyReviewEffort(null, null).Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void A_project_or_task_value_still_beats_a_pass_value()
    {
        DaemonOptions options = new()
        {
            EffortByRole = new RoleEffortDefaults { ReviewVerify = "low", ReviewFinalFullPass = "low" },
        };

        options.ResolveVerifyReviewEffort(null, AgentEffort.High).Should().Be(AgentEffort.High);
        options.ResolveFinalFullPassReviewEffort(AgentEffort.Medium, AgentEffort.High).Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public void A_verify_pass_with_nothing_set_at_any_level_falls_through_to_the_node_wide_value_then_unset()
    {
        new DaemonOptions().ResolveVerifyReviewEffort(null, null).Should().Be(AgentEffort.Unknown);
        new DaemonOptions { Effort = "medium" }.ResolveVerifyReviewEffort(null, null).Should().Be(AgentEffort.Medium);
    }
}
