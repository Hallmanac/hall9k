using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The shape a review cycle's dispatch took (task: review cycles after the first) — parsed
/// tolerantly, and defaulting to <see cref="ReviewMode.Discovery"/> for anything a stream did
/// not record, the same reading every other closed-set value object in this run gives an
/// unrecorded fact.
/// </summary>
public sealed class ReviewModeTests
{
    [Fact]
    public void An_unrecorded_mode_reads_as_discovery()
    {
        ReviewMode.Parse(null).Should().Be(ReviewMode.Discovery);
        ReviewMode.Parse("").Should().Be(ReviewMode.Discovery);
        ReviewMode.Parse("something nobody wrote").Should().Be(ReviewMode.Discovery);
    }

    [Fact]
    public void Each_named_mode_round_trips_through_its_own_word()
    {
        ReviewMode.Parse(ReviewMode.Discovery.Value).Should().Be(ReviewMode.Discovery);
        ReviewMode.Parse(ReviewMode.Verify.Value).Should().Be(ReviewMode.Verify);
        ReviewMode.Parse(ReviewMode.FinalFullPass.Value).Should().Be(ReviewMode.FinalFullPass);
    }

    [Fact]
    public void Implicit_conversion_from_a_blank_string_is_discovery_too()
    {
        ReviewMode fromNull = (string?)null;
        ReviewMode fromEmpty = "";
        fromNull.Should().Be(ReviewMode.Discovery);
        fromEmpty.Should().Be(ReviewMode.Discovery);
    }
}
