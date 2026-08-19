using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class PullRequestUrlsTests
{
    [Theory]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11/")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11?foo=bar")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11#issuecomment-1")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11/?foo=bar#top")]
    public void The_number_survives_human_pasted_url_noise(string url) =>
        PullRequestUrls.ParseNumber(url).Should().Be(11);

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://github.com/Hallmanac/hall9k/pulls")]
    [InlineData("https://github.com/")]
    public void Anything_without_a_number_yields_zero_never_a_guess(string url) =>
        PullRequestUrls.ParseNumber(url).Should().Be(0);
}
