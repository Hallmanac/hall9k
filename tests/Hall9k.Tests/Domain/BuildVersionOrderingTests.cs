using FluentAssertions;
using Hall9k.Domain.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="BuildVersionOrdering"/> compares two <see cref="AssemblyInformationalVersion"/>-shaped
/// strings — the numeric <c>major.minor.patch</c> first, then, when that matches, the
/// <c>git describe</c> commit distance a local install stamps — never guessing at an order it
/// cannot support.
/// </summary>
public sealed class BuildVersionOrderingTests
{
    [Theory]
    [InlineData("0.10.27", "0.10.31", true)]
    [InlineData("0.10.31", "0.10.31", false)]
    [InlineData("0.10.32", "0.10.31", false)]
    [InlineData("0.9.99", "0.10.0", true)]
    public void Orders_two_numeric_versions(string candidate, string than, bool expected) =>
        BuildVersionOrdering.IsOlderThan(candidate, than).Should().Be(expected);

    [Fact]
    public void A_prerelease_suffix_is_stripped_before_comparing() =>
        BuildVersionOrdering.IsOlderThan("0.10.27-rc1", "0.10.31").Should().BeTrue();

    [Fact]
    public void An_unparseable_value_on_either_side_is_never_read_as_older()
    {
        BuildVersionOrdering.IsOlderThan("not-a-version", "0.10.31").Should().BeFalse();
        BuildVersionOrdering.IsOlderThan("0.10.27", "not-a-version").Should().BeFalse();
    }

    [Fact]
    public void A_later_local_build_on_the_same_tag_is_newer_than_an_earlier_one() =>
        BuildVersionOrdering.IsOlderThan("0.10.32-2-gabc1234", "0.10.32-6-gdef5678").Should().BeTrue();

    [Fact]
    public void The_same_commit_distance_on_the_same_tag_is_not_older() =>
        BuildVersionOrdering.IsOlderThan("0.10.32-6-gabc1234", "0.10.32-6-gdef5678").Should().BeFalse();

    [Fact]
    public void An_exact_tag_build_is_older_than_a_local_build_past_that_tag() =>
        BuildVersionOrdering.IsOlderThan("0.10.32", "0.10.32-6-gdef5678").Should().BeTrue();

    [Fact]
    public void A_dirty_local_build_compares_by_its_own_distance() =>
        BuildVersionOrdering.IsOlderThan("0.10.32-2-gabc1234-dirty", "0.10.32-6-gdef5678-dirty").Should().BeTrue();
}
