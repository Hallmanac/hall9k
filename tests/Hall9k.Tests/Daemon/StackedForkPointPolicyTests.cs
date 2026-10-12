using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The question closeout's <c>ParentMergedAligned</c> arm asks before it reopens a stacked child for
/// a no-op replay: has this run's own pre-push rebase already moved its recorded fork point onto the
/// base tip the branch sits on? A pure function of two commit ids, so no repository is involved.
/// </summary>
public sealed class StackedForkPointPolicyTests
{
    private const string BaseTip = "6a3b0009473f8e1c2d4b5a69788899aabbccddee";

    [Fact]
    public void A_fork_point_the_pre_push_rebase_already_moved_onto_the_tip_owes_no_replay()
    {
        StackedForkPointPolicy.ForkPointAlreadyOnBaseTip(BaseTip, BaseTip).Should().BeTrue();
    }

    [Fact]
    public void A_fork_point_still_at_the_parents_old_head_keeps_the_replay_that_moves_it()
    {
        StackedForkPointPolicy
            .ForkPointAlreadyOnBaseTip("1111111111111111111111111111111111111111", BaseTip)
            .Should().BeFalse("a fix session that rebased by hand never moved the recorded fork point");
    }

    [Fact]
    public void A_fork_point_on_an_earlier_base_tip_than_the_one_observed_keeps_the_replay()
    {
        StackedForkPointPolicy
            .ForkPointAlreadyOnBaseTip("2222222222222222222222222222222222222222", BaseTip)
            .Should().BeFalse("the base moved again after the rebase, so the recorded fork point is behind it");
    }

    [Theory]
    [InlineData("", BaseTip)]
    [InlineData(BaseTip, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void A_commit_that_was_never_recorded_or_observed_is_no_evidence_of_a_moved_fork_point(
        string recordedForkPoint, string observedBaseTip)
    {
        StackedForkPointPolicy.ForkPointAlreadyOnBaseTip(recordedForkPoint, observedBaseTip).Should().BeFalse();
    }

    [Fact]
    public void The_comparison_ignores_case_and_surrounding_whitespace()
    {
        StackedForkPointPolicy.ForkPointAlreadyOnBaseTip($" {BaseTip.ToUpperInvariant()}\n", BaseTip).Should().BeTrue();
    }
}
