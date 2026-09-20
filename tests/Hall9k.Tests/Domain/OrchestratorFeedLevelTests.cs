using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The orchestrator feed's own level vocabulary (idea 89471598, piece 2): the three bands'
/// names, a default that does not need to be chosen, and a typo that is refused rather than read
/// as the narrowest band. That the bands nest, which is <see cref="OrchestratorFeedLevel.Admits"/>
/// rather than the vocabulary, is pinned in
/// <see cref="OrchestratorFeedInterestTests.The_bands_nest_so_a_wider_level_admits_everything_a_narrower_one_does"/>,
/// which drives that same method through every level-by-band pair there is.
/// </summary>
public sealed class OrchestratorFeedLevelTests
{
    [Theory]
    [InlineData("actionable", "Actionable")]
    [InlineData("Transitions", "Transitions")]
    [InlineData("  EVERYTHING ", "Everything")]
    public void A_human_typed_band_parses_case_insensitively(string typed, string expected) =>
        OrchestratorFeedLevel.Parse(typed).Value.Should().Be(expected);

    [Fact]
    public void Nothing_typed_is_the_default_band() =>
        OrchestratorFeedLevel.Parse(null).Should().Be(OrchestratorFeedLevel.Transitions);

    [Fact]
    public void A_typo_is_refused_and_the_refusal_names_every_band()
    {
        Action act = () => OrchestratorFeedLevel.Parse("actionble");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*actionable, transitions, or everything*");
    }

    [Fact]
    public void A_value_off_the_stream_round_trips_as_itself() =>
        ((OrchestratorFeedLevel)"Everything").Should().Be(OrchestratorFeedLevel.Everything);

    [Fact]
    public void A_blank_off_the_stream_is_the_default() =>
        ((OrchestratorFeedLevel)(string?)null).Should().Be(OrchestratorFeedLevel.Default);
}
