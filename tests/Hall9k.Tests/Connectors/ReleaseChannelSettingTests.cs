using FluentAssertions;
using Hall9k.Connectors.Releases;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// How the node's release channel is read out of the platform config file, against a real file in
/// a scratch home. The failure direction is always cleared with a one-line warning, never an
/// exception, since a throw here would fail <c>h9k update</c> or stop the daemon from starting.
/// </summary>
public sealed class ReleaseChannelSettingTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    [Fact]
    public async Task A_config_file_naming_the_all_channel_resolves_to_all_without_a_warning()
    {
        WriteConfig("""{"hall9k":{"releaseChannel":"all"}}""");

        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.All);
        resolution.Warning.Should().BeNull();
    }

    [Fact]
    public async Task A_config_file_that_does_not_set_the_channel_resolves_to_cleared_without_a_warning()
    {
        WriteConfig("""{"hall9k":{"maxConcurrentTaskRuns":2}}""");

        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.Cleared);
        resolution.Warning.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_config_file_resolves_to_cleared_with_a_one_line_warning()
    {
        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.Cleared);
        resolution.Warning.Should().NotBeNull().And.Contain("no config file").And.Contain("using cleared");
        resolution.Warning.Should().NotContain("\n");
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    public async Task An_unreadable_config_file_resolves_to_cleared_with_a_one_line_warning(string content)
    {
        WriteConfig(content);

        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.Cleared);
        resolution.Warning.Should().NotBeNull().And.Contain("using cleared");
        resolution.Warning.Should().NotContain("\n");
    }

    [Theory]
    [InlineData("""{"hall9k":{"releaseChannel":"everything"}}""")]
    [InlineData("""{"hall9k":{"releaseChannel":""}}""")]
    [InlineData("""{"hall9k":{"releaseChannel":{"nested":true}}}""")]
    [InlineData("""{"hall9k":{"releaseChannel":5}}""")]
    public async Task An_unrecognized_channel_value_resolves_to_cleared_with_a_one_line_warning(string content)
    {
        WriteConfig(content);

        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.Cleared);
        resolution.Warning.Should().NotBeNull().And.Contain("using cleared");
        resolution.Warning.Should().NotContain("\n");
    }

    [Fact]
    public async Task A_malformed_neighbouring_setting_does_not_take_the_channel_with_it()
    {
        WriteConfig("""{"hall9k":{"releaseChannel":"all","modelByRole":"not-an-object"}}""");

        ReleaseChannelResolution resolution = await ReleaseChannelSetting.ReadAsync(CancellationToken.None);

        resolution.Channel.Should().Be(ReleaseChannel.All);
        resolution.Warning.Should().BeNull();
    }

    [Fact]
    public async Task A_change_to_the_file_is_seen_by_the_next_read_with_no_restart()
    {
        WriteConfig("""{"hall9k":{"releaseChannel":"cleared"}}""");
        (await ReleaseChannelSetting.ReadAsync(CancellationToken.None)).Channel.Should().Be(ReleaseChannel.Cleared);

        WriteConfig("""{"hall9k":{"releaseChannel":"all"}}""");

        (await ReleaseChannelSetting.ReadAsync(CancellationToken.None)).Channel.Should().Be(ReleaseChannel.All);
    }

    [Fact]
    public async Task What_h9k_config_set_writes_is_what_the_lookup_reads_back()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => operating.ReleaseChannel = ReleaseChannel.All.Value, CancellationToken.None);

        File.ReadAllText(Hall9kDatabase.ConfigFile).Should().Contain("\"releaseChannel\": \"all\"");
        (await ReleaseChannelSetting.ReadAsync(CancellationToken.None)).Channel.Should().Be(ReleaseChannel.All);
    }

    private static void WriteConfig(string content) => File.WriteAllText(Hall9kDatabase.ConfigFile, content);
}
