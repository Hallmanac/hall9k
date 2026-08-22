using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Comparing two stated finding locations as places rather than as strings (Decisions Log #62),
/// which is what keeps one pre-existing defect from becoming one draft bug task per cycle — and,
/// just as deliberately, what keeps two different defects in one file from being collapsed into
/// one.
/// </summary>
public sealed class ReviewFindingLocationsTests
{
    [Theory]
    [InlineData("src/Legacy.cs:40", "src/Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "./src/Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "src\\Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "  src/Legacy.cs:40  ")]
    [InlineData("src/Legacy.cs:40", "src/legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "Hall9k/src/Legacy.cs:40")]
    [InlineData("C:/work/src/Legacy.cs:40", "src/Legacy.cs:40")]
    public void One_place_written_two_ways_is_one_place(string left, string right) =>
        ReviewFindingLocations.SamePlace(left, right).Should().BeTrue();

    /// <summary>
    /// The boundary, and it is a choice rather than an oversight: a shifted line comes back as a
    /// second draft, which a human discards in a moment, where matching on the file alone would
    /// swallow a genuinely different defect in a file this run had already routed one from.
    /// </summary>
    [Theory]
    [InlineData("src/Legacy.cs:40", "src/Legacy.cs:43")]
    [InlineData("src/Legacy.cs:40", "src/Legacy.cs:40-52")]
    [InlineData("src/Legacy.cs:40", "src/Legacy.cs")]
    [InlineData("src/Legacy.cs:40", "tests/Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", "src/Other.cs:40")]
    public void Two_places_stay_two_places(string left, string right) =>
        ReviewFindingLocations.SamePlace(left, right).Should().BeFalse();

    /// <summary>
    /// A finding the reviewer never placed matches nothing, itself included — and naming only a
    /// file is not placing it, because the whole point of the boundary above is that a file is
    /// not a place. Neither kind can be shown to be a defect already recorded, and pretending
    /// otherwise would collapse findings that have nothing in common but the reviewer's silence
    /// about where they live: two different out-of-scope defects in one legacy file, each
    /// written `src/Legacy.cs` with no line, would leave one of them routed nowhere and recorded
    /// nowhere.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "src/Legacy.cs:40")]
    [InlineData("src/Legacy.cs:40", null)]
    [InlineData("src/Legacy.cs", "src/Legacy.cs")]
    [InlineData("src/Legacy.cs", "Legacy.cs")]
    public void An_unplaced_finding_matches_nothing(string? left, string? right) =>
        ReviewFindingLocations.SamePlace(left, right).Should().BeFalse();
}
