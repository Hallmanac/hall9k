using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class DaemonStartingMarkerTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("hall9k-startingmarker-").FullName;

    private string MarkerPath => Path.Combine(_directory, "h9kd.starting");

    [Fact]
    public void Missing_marker_indicates_no_recent_launch()
    {
        DaemonStartingMarker.IndicatesRecentLaunch(MarkerPath, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Fresh_marker_indicates_a_recent_launch()
    {
        DateTimeOffset spawnedAt = DateTimeOffset.UtcNow;
        DaemonStartingMarker.Write(MarkerPath, spawnedAt);

        DaemonStartingMarker.IndicatesRecentLaunch(MarkerPath, spawnedAt + TimeSpan.FromSeconds(15))
            .Should().BeTrue("15s is well inside the grace period, matching the field-observed Windows boot time");
    }

    [Fact]
    public void Marker_older_than_the_grace_period_reads_as_no_recent_launch()
    {
        DateTimeOffset spawnedAt = DateTimeOffset.UtcNow;
        DaemonStartingMarker.Write(MarkerPath, spawnedAt);

        DaemonStartingMarker.IndicatesRecentLaunch(
                MarkerPath, spawnedAt + DaemonStartingMarker.GracePeriod + TimeSpan.FromSeconds(1))
            .Should().BeFalse("a spawn that never wrote a pid file within the grace period is not still starting");
    }

    [Fact]
    public void Corrupt_marker_reads_as_no_recent_launch_not_an_error()
    {
        File.WriteAllText(MarkerPath, "not json at all {");

        DaemonStartingMarker.IndicatesRecentLaunch(MarkerPath, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Delete_is_idempotent()
    {
        DaemonStartingMarker.Write(MarkerPath, DateTimeOffset.UtcNow);

        DaemonStartingMarker.Delete(MarkerPath);
        DaemonStartingMarker.Delete(MarkerPath);

        File.Exists(MarkerPath).Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
