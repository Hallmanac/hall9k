using FluentAssertions;
using Hall9k.Connectors.Releases;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// How long the daemon waits between release checks, read out of the platform config file in a
/// scratch home: six hours when nothing says otherwise, never below one hour, and read afresh each
/// time so <c>h9k config set --release-check-interval-hours</c> needs no daemon restart. Nothing
/// here throws, since a daemon that cannot parse its settings must keep checking at the default.
/// </summary>
public sealed class ReleaseCheckIntervalSettingTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    [Fact]
    public void An_unset_interval_is_six_hours() =>
        ReleaseCheckIntervalSetting.Interpret(null).Should().Be(TimeSpan.FromHours(6));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(24, 24)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(8760, 8760)]
    [InlineData(8761, 8760)]
    [InlineData(int.MaxValue, 8760)]
    public void A_configured_interval_is_whole_hours_between_one_hour_and_a_year(int configured, int expectedHours) =>
        ReleaseCheckIntervalSetting.Interpret(configured).Should().Be(TimeSpan.FromHours(expectedHours));

    [Fact]
    public async Task A_missing_config_file_leaves_the_six_hour_default() =>
        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(6));

    [Fact]
    public async Task A_config_file_that_does_not_set_the_interval_leaves_the_default()
    {
        WriteConfig("""{"hall9k":{"maxConcurrentTaskRuns":2}}""");

        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task A_configured_interval_is_read_back_and_a_hand_edited_value_below_the_floor_counts_as_the_floor()
    {
        WriteConfig("""{"hall9k":{"releaseCheckIntervalHours":3}}""");
        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(3));

        WriteConfig("""{"hall9k":{"releaseCheckIntervalHours":0}}""");
        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"hall9k":{"releaseCheckIntervalHours":"soon"}}""")]
    [InlineData("""{"hall9k":{"releaseCheckIntervalHours":{"nested":true}}}""")]
    public async Task A_config_file_that_cannot_be_read_leaves_the_default_rather_than_throwing(string content)
    {
        WriteConfig(content);

        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task A_malformed_neighbouring_setting_does_not_take_the_interval_with_it()
    {
        WriteConfig("""{"hall9k":{"releaseCheckIntervalHours":2,"modelByRole":"not-an-object"}}""");

        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task A_change_to_the_file_is_seen_by_the_next_read_with_no_restart()
    {
        WriteConfig("""{"hall9k":{"releaseCheckIntervalHours":6}}""");
        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(6));

        WriteConfig("""{"hall9k":{"releaseCheckIntervalHours":1}}""");

        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task What_h9k_config_set_writes_is_what_the_loop_reads_back()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => operating.ReleaseCheckIntervalHours = 12, CancellationToken.None);

        File.ReadAllText(Hall9kDatabase.ConfigFile).Should().Contain("\"releaseCheckIntervalHours\": 12");
        (await ReleaseCheckIntervalSetting.ReadAsync(CancellationToken.None)).Should().Be(TimeSpan.FromHours(12));
    }

    private static void WriteConfig(string content) => File.WriteAllText(Hall9kDatabase.ConfigFile, content);
}
