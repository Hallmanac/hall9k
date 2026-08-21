using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Whether a pushed fix earns the reviewers another pass is a chain, not a flag (Decisions
/// Log #62): the project answers first, then the owner, then the node, and off is what
/// nobody answering means. The default matters as much as the chain, because every pass
/// costs review quota.
/// </summary>
public sealed class ReviewRerequestPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nobody_answering_means_off()
    {
        ReviewRerequestPolicy.Resolve(null, null, null).Should().Be(ReviewRerequestPolicy.Disabled);
        ReviewRerequestPolicy.Resolve(
                ReviewRerequestPolicy.Unknown, ReviewRerequestPolicy.Unknown, string.Empty)
            .Should().Be(ReviewRerequestPolicy.Disabled, "an unset level defers; it does not opt in");

        new DaemonOptions().DefaultReviewRerequest.Should().Be(
            ReviewRerequestPolicy.Disabled, "the shipped node default is off, so the cost is always chosen");
    }

    [Fact]
    public void An_owner_preference_carries_where_the_project_says_nothing()
    {
        ReviewRerequestPolicy.Resolve(
                ReviewRerequestPolicy.Unknown, ReviewRerequestPolicy.Enabled, ReviewRerequestPolicy.Disabled)
            .Should().Be(ReviewRerequestPolicy.Enabled, "this owner wants their own work countersigned");
    }

    /// <summary>
    /// The model-policy shape (#33) applied to a narrower question: the more specific level
    /// wins, and a repository is more specific than the person who owns it.
    /// </summary>
    [Fact]
    public void The_project_outranks_the_owner_which_outranks_the_node()
    {
        ReviewRerequestPolicy.Resolve(
                ReviewRerequestPolicy.Disabled, ReviewRerequestPolicy.Enabled, ReviewRerequestPolicy.Enabled)
            .Should().Be(ReviewRerequestPolicy.Disabled, "not in this repository beats a standing preference");

        ReviewRerequestPolicy.Resolve(
                ReviewRerequestPolicy.Enabled, ReviewRerequestPolicy.Disabled, ReviewRerequestPolicy.Disabled)
            .Should().Be(ReviewRerequestPolicy.Enabled);

        ReviewRerequestPolicy.Resolve(null, null, ReviewRerequestPolicy.Enabled)
            .Should().Be(ReviewRerequestPolicy.Enabled, "the node default is the floor, not a veto");
    }

    [Theory]
    [InlineData("on", "Enabled")]
    [InlineData("ENABLED", "Enabled")]
    [InlineData("off", "Disabled")]
    [InlineData("no", "Disabled")]
    [InlineData("default", "")]
    [InlineData("sometimes", "")]
    [InlineData(null, "")]
    public void Input_maps_to_the_closed_set_and_never_guesses(string? input, string expected) =>
        ReviewRerequestPolicy.FromInput(input).Value.Should().Be(expected);

    [Fact]
    public void An_owner_records_and_clears_the_preference_through_the_decider()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Brian", "brian@example.com", Now));
        owner.ReviewRerequest.Should().Be(ReviewRerequestPolicy.Unknown, "a registration states no preference");

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now));
        owner.ReviewRerequest.Should().Be(ReviewRerequestPolicy.Enabled);

        // Unmentioned means left alone, which is what Optional buys over a nullable field.
        owner.Apply(OwnerDecider.ChangeSettings(owner, Optional<ReviewRerequestPolicy>.None, Now.AddDays(1)));
        owner.ReviewRerequest.Should().Be(ReviewRerequestPolicy.Enabled);

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Unknown), Now.AddDays(2)));
        owner.ReviewRerequest.Should().Be(
            ReviewRerequestPolicy.Unknown, "Unknown is the clearing idiom, so the levels around it decide again");
    }

    [Fact]
    public void A_policy_outside_the_closed_set_is_refused_rather_than_stored()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Brian", null, Now));

        Action change = () => OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.Of("Sometimes"), Now);

        change.Should().Throw<DomainValidationException>().WithMessage("*Enabled*Disabled*");
    }
}
