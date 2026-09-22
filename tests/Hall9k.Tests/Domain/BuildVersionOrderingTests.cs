using FluentAssertions;
using Hall9k.Domain.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="BuildVersionOrdering"/> compares two <see cref="AssemblyInformationalVersion"/>-shaped
/// strings — numeric-only, never guessing at an order it cannot support.
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
}
