using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The shape a review cycle's dispatch took (task: review cycles after the first) — blank
/// defaults to <see cref="ReviewMode.Discovery"/> for anything a stream did not record, and
/// anything else, recognized or not, round-trips as itself (TASK-MODEL.md §8), the same
/// preservation <see cref="ReviewSeverity"/> and <see cref="ReviewLens"/> already give their own
/// unrecognized payload words.
/// </summary>
public sealed class ReviewModeTests
{
    [Fact]
    public void An_unrecorded_mode_reads_as_discovery()
    {
        ReviewMode fromNull = (string?)null;
        ReviewMode fromEmpty = "";
        fromNull.Should().Be(ReviewMode.Discovery);
        fromEmpty.Should().Be(ReviewMode.Discovery);
    }

    [Fact]
    public void Each_named_mode_round_trips_through_its_own_word()
    {
        ((ReviewMode)ReviewMode.Discovery.Value).Should().Be(ReviewMode.Discovery);
        ((ReviewMode)ReviewMode.Verify.Value).Should().Be(ReviewMode.Verify);
        ((ReviewMode)ReviewMode.FinalFullPass.Value).Should().Be(ReviewMode.FinalFullPass);
    }

    [Fact]
    public void An_unrecognized_mode_round_trips_as_itself_rather_than_collapsing_to_discovery()
    {
        ReviewMode unrecognized = "FinalFullPass2";
        unrecognized.Value.Should().Be("FinalFullPass2");
        unrecognized.Should().NotBe(ReviewMode.Discovery);
        unrecognized.Should().NotBe(ReviewMode.FinalFullPass);
    }
}
