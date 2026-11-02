using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>The feedback check's place in the model policy: a role of its own, so it and the refinement it may lead to are set to different tiers.</summary>
public sealed class FeedbackCheckRoleTests
{
    [Fact]
    public void The_check_has_a_role_of_its_own_so_it_and_the_refinement_are_set_to_different_tiers_without_a_code_change()
    {
        DaemonOptions options = new();
        options.ResolveFeedbackCheckModel().Value.Should().Be(AgentModel.FeedbackCheckDefault, "unset, the check runs on its own cheap floor");
        options.ResolveFeedbackCheckEffort().Should().Be(AgentEffort.Medium);

        options.ModelByRole.FeedbackCheck = "claude-haiku-5-5";
        options.ModelByRole.Refinement = "claude-opus-5-5";
        options.EffortByRole.FeedbackCheck = "low";
        options.EffortByRole.Refinement = "high";

        options.ResolveFeedbackCheckModel().Value.Should().Be("claude-haiku-5-5");
        options.ResolveModel(AgentRole.Refinement, null, null).Value.Should().Be("claude-opus-5-5");
        options.ResolveFeedbackCheckEffort().Should().Be(AgentEffort.Low);
        options.ResolveEffort(AgentRole.Refinement, null, null).Should().Be(AgentEffort.High);
    }
}
