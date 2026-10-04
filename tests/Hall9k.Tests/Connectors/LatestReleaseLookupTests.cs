using System.ComponentModel;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Releases;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The shared release lookup over a fake <c>gh</c> (house rule: no test touches GitHub). The fake
/// holds a list of releases in the order <c>gh release list</c> returns them (newest created
/// first) and answers <c>release view</c> from the same list, with the cleared channel's tag-less
/// view answering with whichever release is marked latest.
/// </summary>
public sealed class LatestReleaseLookupTests
{
    private const string Rid = "osx-arm64";
    private const string Archive = "hall9k-osx-arm64.tar.gz";

    [Fact]
    public async Task The_cleared_channel_returns_githubs_latest_release_and_never_lists()
    {
        FakeGh gh = new(
            FakeRelease.Cleared("v0.10.46"),
            FakeRelease.PreRelease("v0.10.47"));

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.Cleared).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.Found);
        result.Tag.Should().Be("v0.10.46");
        gh.Calls.Should().ContainSingle().Which.Should().StartWith("release view --repo o/r");
    }

    [Fact]
    public async Task The_all_channel_returns_the_highest_version_even_when_a_lower_one_was_created_later()
    {
        // A patch cut from an older tag is created after a newer minor: creation order is not version order.
        FakeGh gh = new(
            FakeRelease.PreRelease("v0.10.9"),
            FakeRelease.Cleared("v0.10.46"),
            FakeRelease.PreRelease("v0.9.200"));

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.Found);
        result.Tag.Should().Be("v0.10.46", "0.10.46 outranks 0.10.9 numerically, and the newest-created release is neither");
    }

    [Fact]
    public async Task The_all_channel_sees_a_pre_release_that_the_cleared_channel_does_not()
    {
        FakeGh gh = new(
            FakeRelease.PreRelease("v0.10.47"),
            FakeRelease.Cleared("v0.10.46"));

        LatestReleaseResult all = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);
        LatestReleaseResult cleared = await Lookup(gh, ReleaseChannel.Cleared).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        all.Tag.Should().Be("v0.10.47");
        cleared.Tag.Should().Be("v0.10.46");
    }

    [Fact]
    public async Task The_all_channel_views_the_chosen_tag_to_read_its_assets_because_the_list_has_none()
    {
        FakeGh gh = new(FakeRelease.PreRelease("v0.10.47"), FakeRelease.Cleared("v0.10.46"));

        await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        gh.Calls.Should().HaveCount(2);
        gh.Calls[0].Should().StartWith("release list --repo o/r");
        gh.Calls[1].Should().StartWith("release view v0.10.47 --repo o/r");
    }

    [Fact]
    public async Task A_draft_never_counts_even_when_it_carries_the_highest_version()
    {
        FakeGh gh = new(
            FakeRelease.Draft("v0.10.48"),
            FakeRelease.PreRelease("v0.10.47"));

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Tag.Should().Be("v0.10.47");
    }

    [Fact]
    public async Task The_all_channel_ignores_a_tag_that_is_not_a_version()
    {
        FakeGh gh = new(FakeRelease.PreRelease("nightly"), FakeRelease.PreRelease("v0.10.47"));

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Tag.Should().Be("v0.10.47");
    }

    [Fact]
    public async Task The_all_channel_with_nothing_published_says_there_is_no_release()
    {
        FakeGh gh = new(FakeRelease.Draft("v0.10.48"));

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.NoRelease);
        result.Tag.Should().BeNull();
    }

    [Theory]
    [InlineData("cleared", Archive)]
    [InlineData("cleared", "checksums.txt")]
    [InlineData("all", Archive)]
    [InlineData("all", "checksums.txt")]
    public async Task A_release_missing_this_platforms_archive_or_the_checksums_is_not_yet_complete_rather_than_a_tag(
        string channel, string missingAsset)
    {
        FakeRelease release = FakeRelease.Cleared("v0.10.47") with
        {
            Assets = [.. FakeRelease.CompleteAssets.Where(asset => asset != missingAsset)],
        };
        FakeGh gh = new(release);

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.FromInput(channel)).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.NotYetComplete);
        result.Tag.Should().BeNull();
        result.CandidateTag.Should().Be("v0.10.47");
        result.MissingAssets.Should().Equal(missingAsset);
    }

    [Fact]
    public async Task Another_platforms_archive_does_not_make_a_release_complete_for_this_one()
    {
        FakeRelease release = FakeRelease.Cleared("v0.10.47") with { Assets = ["hall9k-linux-x64.tar.gz", "checksums.txt"] };

        LatestReleaseResult result = await Lookup(new FakeGh(release), ReleaseChannel.Cleared)
            .ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.NotYetComplete);
        result.MissingAssets.Should().Equal(Archive);
    }

    [Theory]
    [InlineData("cleared")]
    [InlineData("all")]
    public async Task A_missing_gh_is_reported_as_gh_missing(string channel)
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46")) { Throws = new Win32Exception("No such file or directory") };

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.FromInput(channel)).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.GhMissing);
        result.Tag.Should().BeNull();
    }

    [Theory]
    [InlineData("cleared", "gh release view")]
    [InlineData("all", "gh release list")]
    public async Task A_gh_that_exits_non_zero_is_reported_with_its_stderr_and_the_command_that_failed(string channel, string command)
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46")) { Failure = new ProcessResult(1, string.Empty, "HTTP 401: Bad credentials") };

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.FromInput(channel)).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.GhFailed);
        result.Command.Should().Be(command);
        result.Detail.Should().Contain("HTTP 401: Bad credentials");
    }

    [Theory]
    [InlineData("cleared")]
    [InlineData("all")]
    public async Task A_gh_that_never_answers_is_reported_as_a_timeout(string channel)
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46")) { Throws = new TimeoutException("gh did not answer within 00:02:00") };

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.FromInput(channel)).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.TimedOut);
        result.Detail.Should().Contain("did not answer");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"assets":[]}""")]
    [InlineData("""{"tagName":"v0.10.46"}""")]
    [InlineData("""{"tagName":"","assets":[]}""")]
    [InlineData("""{"tagName":"v0.10.46","assets":[{"size":3}]}""")]
    public async Task Unreadable_view_output_is_reported_as_unparseable_on_the_cleared_channel(string output)
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46")) { ViewOutput = output };

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.Cleared).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.Unparseable);
        result.Tag.Should().BeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"tagName":"v0.10.46"}""")]
    [InlineData("""[{"tagName":"v0.10.46"}]""")]
    public async Task Unreadable_list_output_is_reported_as_unparseable_on_the_all_channel(string output)
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46")) { ListOutput = output };

        LatestReleaseResult result = await Lookup(gh, ReleaseChannel.All).ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Outcome.Should().Be(LatestReleaseOutcome.Unparseable);
        result.Command.Should().Be("gh release list");
    }

    [Fact]
    public async Task The_channel_is_read_at_each_call_so_a_change_takes_effect_without_a_restart()
    {
        FakeGh gh = new(FakeRelease.PreRelease("v0.10.47"), FakeRelease.Cleared("v0.10.46"));
        ReleaseChannel channel = ReleaseChannel.Cleared;
        LatestReleaseLookup lookup = new(
            gh.Runner, _ => Task.FromResult(new ReleaseChannelResolution(channel, null)));

        LatestReleaseResult before = await lookup.ResolveAsync("o/r", Rid, ".", CancellationToken.None);
        channel = ReleaseChannel.All;
        LatestReleaseResult after = await lookup.ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        before.Tag.Should().Be("v0.10.46");
        after.Tag.Should().Be("v0.10.47");
    }

    [Fact]
    public async Task The_channel_warning_travels_with_the_result()
    {
        FakeGh gh = new(FakeRelease.Cleared("v0.10.46"));
        LatestReleaseLookup lookup = new(
            gh.Runner,
            _ => Task.FromResult(new ReleaseChannelResolution(ReleaseChannel.Cleared, "Release channel: could not use it; using cleared.")));

        LatestReleaseResult result = await lookup.ResolveAsync("o/r", Rid, ".", CancellationToken.None);

        result.Channel.Warning.Should().Be("Release channel: could not use it; using cleared.");
    }

    private static LatestReleaseLookup Lookup(FakeGh gh, ReleaseChannel channel) =>
        new(gh.Runner, _ => Task.FromResult(new ReleaseChannelResolution(channel, null)));

    private sealed record FakeRelease(string Tag, bool IsDraft, bool IsLatest, IReadOnlyList<string> Assets)
    {
        public static readonly IReadOnlyList<string> CompleteAssets =
        [
            "hall9k-osx-arm64.tar.gz", "hall9k-linux-x64.tar.gz", "hall9k-win-x64.zip", "hall9k-win-arm64.zip", "checksums.txt",
        ];

        public static FakeRelease Cleared(string tag) => new(tag, IsDraft: false, IsLatest: true, CompleteAssets);

        public static FakeRelease PreRelease(string tag) => new(tag, IsDraft: false, IsLatest: false, CompleteAssets);

        public static FakeRelease Draft(string tag) => new(tag, IsDraft: true, IsLatest: false, CompleteAssets);
    }

    private sealed class FakeGh(params FakeRelease[] releases)
    {
        /// <summary>Every call as the argument string, in order, so a test can assert which verbs ran.</summary>
        public List<string> Calls { get; } = [];

        public Exception? Throws { get; init; }

        public ProcessResult? Failure { get; init; }

        public string? ViewOutput { get; init; }

        public string? ListOutput { get; init; }

        public ProcessRunner Runner => (fileName, arguments, _, _) =>
        {
            fileName.Should().Be("gh");
            List<string> argumentList = [.. arguments];
            Calls.Add(string.Join(' ', argumentList));

            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(Failure ?? argumentList switch
            {
                ["release", "list", ..] => new ProcessResult(0, ListOutput ?? ListJson(), string.Empty),
                ["release", "view", var first, ..] when !first.StartsWith("--", StringComparison.Ordinal) =>
                    new ProcessResult(0, ViewOutput ?? ViewJson(releases.Single(release => release.Tag == first)), string.Empty),
                ["release", "view", ..] =>
                    new ProcessResult(0, ViewOutput ?? ViewJson(releases.First(release => release.IsLatest)), string.Empty),
                _ => throw new InvalidOperationException($"FakeGh does not know how to handle: gh {string.Join(' ', argumentList)}"),
            });
        };

        private string ListJson() => JsonSerializer.Serialize(
            releases.Select(release => new { tagName = release.Tag, isDraft = release.IsDraft }));

        private static string ViewJson(FakeRelease release) => JsonSerializer.Serialize(new
        {
            tagName = release.Tag,
            assets = release.Assets.Select(name => new { name }),
        });
    }
}
