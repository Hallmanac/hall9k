using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Every surface that names a boundary's choices renders from <see cref="InteractiveBoundaryChoices"/>,
/// so naming <c>h9k task skip-to-pr</c> here names it in the park text, the starting prompt, and the
/// outbound end-of-phase report alike (idea 6e6d0200, Card 3).
/// </summary>
public sealed class InteractiveBoundaryChoicesTests
{
    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");

    [Theory]
    [InlineData(InteractiveBoundaryLevers.ProceedOrRedirect)]
    [InlineData(InteractiveBoundaryLevers.ReviewVerdictToFix)]
    public void The_boundaries_with_review_left_to_skip_name_the_verb_in_both_renderings(InteractiveBoundaryLevers levers)
    {
        InteractiveBoundaryChoices.Lines(levers, TaskId).Should()
            .ContainSingle(line => line.StartsWith($"`h9k task skip-to-pr {TaskId} --reason"));
        InteractiveBoundaryChoices.ParkText(levers, TaskId).Should().Contain($"h9k task skip-to-pr {TaskId}");
    }

    [Fact]
    public void The_gates_to_pull_request_boundary_does_not_offer_it_because_review_has_already_settled()
    {
        InteractiveBoundaryChoices.Lines(InteractiveBoundaryLevers.GatesToPullRequest, TaskId)
            .Should().NotContain(line => line.Contains("skip-to-pr"));
        InteractiveBoundaryChoices.ParkText(InteractiveBoundaryLevers.GatesToPullRequest, TaskId)
            .Should().NotContain("skip-to-pr");
    }

    [Fact]
    public void A_human_gate_park_gets_the_gates_choices_whatever_phase_it_was_recorded_from()
    {
        InteractiveBoundaryChoices.LeversFor(ReviewPhase.MergeReady, parkedIsHumanGate: true)
            .Should().Be(InteractiveBoundaryLevers.HumanGate);
        InteractiveBoundaryChoices.LeversFor(ReviewPhase.Settling, parkedIsHumanGate: true)
            .Should().Be(InteractiveBoundaryLevers.HumanGate);
        InteractiveBoundaryChoices.LeversFor(ReviewPhase.MergeReady, parkedIsHumanGate: false)
            .Should().Be(InteractiveBoundaryLevers.GatesToPullRequest, "interactive mode's own boundary is unchanged");
    }

    [Fact]
    public void The_human_gate_offers_brief_proceed_both_change_routes_merge_ready_and_the_discard_pair_and_never_names_interactive_mode()
    {
        string lines = string.Join('\n', InteractiveBoundaryChoices.Lines(InteractiveBoundaryLevers.HumanGate, TaskId));
        string park = InteractiveBoundaryChoices.ParkText(InteractiveBoundaryLevers.HumanGate, TaskId);

        foreach (string surface in new[] { lines, park })
        {
            surface.Should().Contain($"h9k review brief {TaskId}")
                .And.Contain($"h9k review proceed {TaskId}")
                .And.Contain($"h9k review resolve {TaskId} --needs-fixes")
                .And.Contain($"h9k review fixed {TaskId}")
                .And.Contain($"h9k review resolve {TaskId} --merge-ready --reason")
                .And.Contain($"h9k task abandon {TaskId}")
                .And.Contain($"h9k task add --evolved-from {TaskId}")
                .And.NotContain("clear-interactive-mode")
                .And.NotContainEquivalentOf("interactive mode is on")
                .And.NotContain("skip-to-pr", "review has settled, so there is nothing left to skip");
        }
    }

    [Fact]
    public void The_verdict_to_fix_park_text_counts_its_five_choices()
    {
        InteractiveBoundaryChoices.ParkText(InteractiveBoundaryLevers.ReviewVerdictToFix, TaskId)
            .Should().StartWith("Five choices:");
    }
}
