using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Whether a GitHub reviewer assignment to this install's own login auto-starts a pr-review task,
/// and how fast — the <see cref="BacklogPolicy"/> shape applied to idea e5e98a33. Parse is the
/// strict form <c>h9k project set --auto-pr-review</c> goes through; the implicit string
/// conversion is the raw, unvalidated wrap every closed-vocabulary value object in this repo uses
/// so that <see cref="Handlers.ProjectDecider"/> stays the one place the closed set is enforced.
/// </summary>
public sealed class AutoPrReviewSpeedTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("Normal")]
    [InlineData("NORMAL")]
    public void Parse_reads_normal_case_insensitively(string value) =>
        AutoPrReviewSpeed.Parse(value).Should().Be(AutoPrReviewSpeed.Normal);

    [Theory]
    [InlineData("first")]
    [InlineData("First")]
    public void Parse_reads_first_case_insensitively(string value) =>
        AutoPrReviewSpeed.Parse(value).Should().Be(AutoPrReviewSpeed.First);

    [Theory]
    [InlineData("now")]
    [InlineData("Now")]
    public void Parse_reads_now_case_insensitively(string value) =>
        AutoPrReviewSpeed.Parse(value).Should().Be(AutoPrReviewSpeed.Now);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("off")]
    [InlineData("Off")]
    public void Parse_reads_blank_or_off_as_off(string? value) =>
        AutoPrReviewSpeed.Parse(value).Should().Be(AutoPrReviewSpeed.Off);

    [Fact]
    public void Parse_refuses_anything_outside_the_vocabulary()
    {
        Action act = () => AutoPrReviewSpeed.Parse("fast");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*off, normal, first, or now*");
    }

    [Fact]
    public void Parse_refuses_a_control_character_without_echoing_it_into_the_refusal()
    {
        Action act = () => AutoPrReviewSpeed.Parse("now‮-evil");

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should().NotContain("‮")
            .And.Contain("now?-evil");
    }

    [Fact]
    public void Parse_refuses_an_unbounded_argument_without_echoing_it_whole()
    {
        Action act = () => AutoPrReviewSpeed.Parse(new string('x', 500));

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("…")
            .And.NotContain(new string('x', 500));
    }

    [Fact]
    public void The_raw_conversion_wraps_without_validating_so_the_decider_can_be_the_one_gate()
    {
        AutoPrReviewSpeed raw = "not-a-real-speed";

        raw.Value.Should().Be("not-a-real-speed");
        raw.Should().NotBe(AutoPrReviewSpeed.Off);
    }

    [Fact]
    public void Off_round_trips_through_the_string_conversion()
    {
        ((string)AutoPrReviewSpeed.Off).Should().Be("Off");
        ((AutoPrReviewSpeed)"Off").Should().Be(AutoPrReviewSpeed.Off);
    }
}
