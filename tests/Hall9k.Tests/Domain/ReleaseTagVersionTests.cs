using FluentAssertions;
using Hall9k.Domain.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>Whether a release tag is newer than the version a binary reports, the one comparison the daemon, h9k status and h9k doctor share.</summary>
public sealed class ReleaseTagVersionTests
{
    [Theory]
    [InlineData("v0.13.1", "0.13.0", true)]
    [InlineData("v0.14.0", "0.13.9", true)]
    [InlineData("v0.13.0", "0.13.0", false)]
    [InlineData("v0.12.9", "0.13.0", false)]
    [InlineData("0.13.1", "0.13.0", true)]
    public void A_tag_is_newer_only_when_its_version_outranks_the_running_one(string tag, string running, bool expected) =>
        ReleaseTagVersion.IsNewerThan(tag, running).Should().Be(expected);

    [Theory]
    [InlineData("v0.13.0", "0.13.0-5-gabc1234")]
    [InlineData("v0.13.0", "0.13.0-5-gabc1234-dirty")]
    public void A_local_build_past_its_tag_is_not_behind_that_tag(string tag, string running) =>
        ReleaseTagVersion.IsNewerThan(tag, running).Should().BeFalse();

    [Fact]
    public void A_local_build_past_a_tag_is_still_behind_the_next_release() =>
        ReleaseTagVersion.IsNewerThan("v0.13.1", "0.13.0-5-gabc1234").Should().BeTrue();

    [Theory]
    [InlineData("not-a-version", "0.13.0")]
    [InlineData("v0.13.1", "not-a-version")]
    [InlineData("v0.13.1", "")]
    public void A_value_that_cannot_be_read_is_never_newer(string tag, string running) =>
        ReleaseTagVersion.IsNewerThan(tag, running).Should().BeFalse();

    [Fact]
    public void Bare_drops_the_leading_v() => ReleaseTagVersion.Bare("v0.13.1").Should().Be("0.13.1");
}
