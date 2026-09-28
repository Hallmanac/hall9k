using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k project show</c>'s "Review requires membership" row (security review idea 6be68ee2,
/// finding 1): an unset override must state the gate's CURRENT effective value, not just what it
/// defers to, so the operator never has to combine this row with the separate visibility row to
/// learn whether the gate is on right now (independent pre-PR review, cycle 1, conformance lens).
/// </summary>
public sealed class ReviewMembershipOptionTests
{
    [Theory]
    [InlineData("on")]
    [InlineData("ON")]
    [InlineData("enabled")]
    [InlineData("true")]
    [InlineData("yes")]
    public void Every_on_spelling_parses_to_enabled(string value) =>
        ReviewMembershipOption.Parse(value).Should().Be(ReviewMembershipPolicy.Enabled);

    [Theory]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("false")]
    [InlineData("no")]
    public void Every_off_spelling_parses_to_disabled(string value) =>
        ReviewMembershipOption.Parse(value).Should().Be(ReviewMembershipPolicy.Disabled);

    [Fact]
    public void Default_clears_the_override_to_unknown() =>
        ReviewMembershipOption.Parse("default").Should().Be(ReviewMembershipPolicy.Unknown);

    [Fact]
    public void An_unrecognized_value_is_refused_rather_than_silently_read_as_unset()
    {
        Action act = () => ReviewMembershipOption.Parse("maybe");

        act.Should().Throw<DomainValidationException>().WithMessage("*on, off, or default*");
    }

    [Fact]
    public void An_explicit_on_always_reads_on_whatever_the_observed_visibility_is()
    {
        string described = ReviewMembershipOption.Describe(
            ReviewMembershipPolicy.Enabled, isPrivate: true, "enabled detail", "unset detail");

        described.Should().Contain("on").And.Contain("enabled detail").And.NotContain("unset");
    }

    [Fact]
    public void An_explicit_off_always_reads_off_whatever_the_observed_visibility_is()
    {
        string described = ReviewMembershipOption.Describe(
            ReviewMembershipPolicy.Disabled, isPrivate: false, "enabled detail", "unset detail");

        described.Should().Contain("off").And.NotContain("unset");
    }

    [Fact]
    public void An_unset_override_on_a_public_repository_states_it_currently_reads_on()
    {
        string described = ReviewMembershipOption.Describe(
            ReviewMembershipPolicy.Unknown, isPrivate: false, "enabled detail", "unset detail");

        described.Should().Contain("unset, currently on").And.Contain("unset detail");
    }

    [Fact]
    public void An_unset_override_on_a_private_repository_states_it_currently_reads_off()
    {
        string described = ReviewMembershipOption.Describe(
            ReviewMembershipPolicy.Unknown, isPrivate: true, "enabled detail", "unset detail");

        described.Should().Contain("unset, currently off");
    }

    /// <summary>
    /// No visibility has ever been observed for this project (a brand-new registration, or a sweep
    /// that has never once succeeded) — the fail-closed default is ON, the identical default
    /// <c>AutoPrReviewObservation.DecideMembershipGate</c> falls back to on a failed read.
    /// </summary>
    [Fact]
    public void An_unset_override_with_no_observed_visibility_at_all_states_the_fail_closed_default_is_on()
    {
        string described = ReviewMembershipOption.Describe(
            ReviewMembershipPolicy.Unknown, isPrivate: null, "enabled detail", "unset detail");

        described.Should().Contain("unset, currently on").And.Contain("fail-closed default");
    }
}
