using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The dispatch tier value object (Decisions Log #141): the closed vocabulary, the clearing word,
/// and the one property the rotation actually compares. Mirrors
/// <see cref="AutoPrReviewSpeedTests"/>, whose conventions this type follows.
/// </summary>
public sealed class ProjectPriorityTests
{
    [Fact]
    public void The_default_tier_is_normal_and_a_blank_reads_as_it()
    {
        ProjectPriority untouched = (string?)null;

        untouched.Should().Be(ProjectPriority.Normal, "a project that never recorded a tier rotates");
        untouched.IsDefault.Should().BeTrue();
        ProjectPriority.Normal.Tier.Should().Be(0);
    }

    [Fact]
    public void The_tiers_order_high_over_normal_over_low()
    {
        ProjectPriority.High.Tier.Should().BeGreaterThan(ProjectPriority.Normal.Tier);
        ProjectPriority.Normal.Tier.Should().BeGreaterThan(ProjectPriority.Low.Tier);
    }

    [Fact]
    public void An_unrecognized_recorded_value_reads_as_unknown_and_schedules_as_the_default()
    {
        // The one thing that must not happen to a tier written by a newer build: it cannot
        // silently starve a project or silently promote one, so Unknown carries the default's
        // own number while still reporting as unrecognized.
        ProjectPriority parsed = ProjectPriority.FromInput("urgent");

        parsed.Should().Be(ProjectPriority.Unknown);
        parsed.Tier.Should().Be(ProjectPriority.Normal.Tier);
        parsed.IsDefault.Should().BeFalse("it schedules like the default without being it");
    }

    [Theory]
    [InlineData("high")]
    [InlineData("HIGH")]
    [InlineData(" high ")]
    public void Parse_reads_a_tier_however_it_was_typed(string input) =>
        ProjectPriority.Parse(input).Should().Be(ProjectPriority.High);

    [Theory]
    [InlineData("default")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_reads_the_clearing_word_as_the_default_tier(string? input) =>
        ProjectPriority.Parse(input).Should().Be(ProjectPriority.Normal);

    [Fact]
    public void Parse_refuses_a_typo_rather_than_quietly_scheduling_it_as_normal()
    {
        // A mistyped focus that silently did nothing is the one scheduling mistake an operator
        // would not notice, which is why the strict form exists at all.
        Action parse = () => ProjectPriority.Parse("hihg");

        parse.Should().Throw<DomainValidationException>()
            .WithMessage("*not a project priority*high, normal, low*");
    }

    [Fact]
    public void Parse_refuses_unknown_by_name_too()
    {
        // Unknown is what a recorded value reads as, never something a human may set.
        Action parse = () => ProjectPriority.Parse("unknown");

        parse.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_refused_tier_is_quoted_back_legibly_and_boundedly()
    {
        // The AutoPrReviewSpeed convention: this value comes off a command line and the refusal
        // is printed to a terminal, so control characters cannot ride into it and an unbounded
        // argument cannot be echoed whole.
        Action parse = () => ProjectPriority.Parse("hi;rm -rf /" + new string('x', 80));

        parse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().NotContain(";").And.NotContain("/").And.Contain("…",
                "an unbounded argument is truncated rather than echoed whole");
    }
}
