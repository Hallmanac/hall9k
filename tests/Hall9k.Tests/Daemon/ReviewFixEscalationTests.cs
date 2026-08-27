using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Whether a fix round repeats the immediately preceding one's own findings (task: a second fix
/// round over the same findings) — conservative by design, so every "should this escalate" case
/// here has a matching "and this near-miss must not" case.
/// </summary>
public sealed class ReviewFixEscalationTests
{
    [Fact]
    public void A_first_round_with_no_previous_round_never_escalates()
    {
        ReviewFixEscalation.Reason([], ["src/Auth.cs:42"], null).Should().BeNull(
            "there is no previous round to repeat");
    }

    [Fact]
    public void A_round_over_the_same_location_the_previous_round_carried_escalates()
    {
        string? reason = ReviewFixEscalation.Reason(
            ["src/Auth.cs:42"], ["src/Auth.cs:42"], null);

        reason.Should().NotBeNull("the automated finding repeats the previous round's own location");
        reason.Should().Contain("src/Auth.cs:42");
    }

    [Fact]
    public void Two_ways_of_writing_the_same_place_still_escalate()
    {
        ReviewFixEscalation.Reason(["src/Auth.cs:42"], ["Auth.cs:42"], null).Should().NotBeNull(
            "ReviewFindingLocations.SamePlace already treats these as one place everywhere else in the loop");
    }

    [Fact]
    public void A_round_over_a_genuinely_different_location_does_not_escalate()
    {
        ReviewFixEscalation.Reason(["src/Auth.cs:42"], ["src/Other.cs:99"], null).Should().BeNull(
            "a fresh defect is not a repeat, whatever round it arrives on");
    }

    [Fact]
    public void A_shifted_line_in_the_same_file_does_not_escalate()
    {
        ReviewFixEscalation.Reason(["src/Auth.cs:42"], ["src/Auth.cs:99"], null).Should().BeNull(
            "a different stated line is a different place, the same boundary ReviewFindingLocations.SamePlace draws");
    }

    [Fact]
    public void A_human_needs_fixes_reason_that_names_the_previous_rounds_location_escalates()
    {
        string? reason = ReviewFixEscalation.Reason(
            ["src/Auth.cs:42"], [], "Still broken — see src/Auth.cs:42, the limiter never resets.");

        reason.Should().NotBeNull("the human's own text restates the previous round's location");
        reason.Should().Contain("src/Auth.cs:42");
    }

    [Fact]
    public void A_human_needs_fixes_reason_that_never_mentions_the_previous_location_does_not_escalate()
    {
        ReviewFixEscalation.Reason(
            ["src/Auth.cs:42"], [], "This is a design decision, not a defect — proceed anyway.").Should().BeNull(
            "when in doubt whether the human is talking about the same defect, do not escalate");
    }

    [Fact]
    public void An_empty_human_reason_alongside_disjoint_locations_does_not_escalate()
    {
        ReviewFixEscalation.Reason(["src/Auth.cs:42"], ["src/Other.cs:99"], null).Should().BeNull();
        ReviewFixEscalation.Reason(["src/Auth.cs:42"], ["src/Other.cs:99"], string.Empty).Should().BeNull();
    }

    [Fact]
    public void A_human_reason_naming_a_different_line_that_prefixes_the_previous_locations_line_does_not_escalate()
    {
        ReviewFixEscalation.Reason(
            ["src/Auth.cs:4"], [], "separate bug, see src/Auth.cs:42").Should().BeNull(
            "src/Auth.cs:42 names a different line than src/Auth.cs:4 — an unbounded substring match " +
            "would wrongly treat the shorter line number as a prefix match");
    }

    [Fact]
    public void A_human_reason_naming_an_unrelated_file_whose_name_ends_in_the_previous_locations_file_does_not_escalate()
    {
        ReviewFixEscalation.Reason(
            ["Engine.cs:512"], [],
            "the leak is in src/Hall9k.Daemon/Review/ReviewEngine.cs:512").Should().BeNull(
            "ReviewEngine.cs:512 and Engine.cs:512 are different places by ReviewFindingLocations.SamePlace — " +
            "an unbounded substring match would wrongly treat the shorter filename as a suffix match");
    }

    [Fact]
    public void A_human_reason_naming_a_specific_line_in_a_file_the_previous_round_named_with_no_line_does_not_escalate()
    {
        ReviewFixEscalation.Reason(
            ["src/Foo.cs"], [], "the actual bug is at src/Foo.cs:120").Should().BeNull(
            "a bare file with no stated line matches nothing per ReviewFindingLocations.SamePlace — an " +
            "unbounded substring match would wrongly treat a more specific file:line as a restatement");
    }
}
