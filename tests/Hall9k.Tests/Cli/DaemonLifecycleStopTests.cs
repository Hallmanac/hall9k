using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// DaemonLifecycle.StopAsync's "nothing to stop" branch (no confirmed pid file) used to
/// leave a starting marker behind untouched, so a spawn that died before ever writing a
/// pid file locked h9k daemon start out as "already starting" for the rest of the
/// marker's 60s grace period, with no way for an operator who had already diagnosed the
/// dead spawn to clear it (pre-PR review, cycle 1; Brian's ruling 2026-09-05). Redirecting
/// HALL9K_HOME to a temp directory keeps this off a developer's real ~/.hall9k.
/// </summary>
public sealed class DaemonLifecycleStopTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();
    private static readonly DeferredDaemonAutostart NoAutostart = new("not supported in this test");

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public async Task Stop_with_no_pid_file_and_no_marker_reports_nothing_to_stop()
    {
        int exitCode = await DaemonLifecycle.StopAsync(NoAutostart, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        File.Exists(DaemonRuntime.StartingMarkerFile).Should().BeFalse();
    }

    [Fact]
    public async Task Stop_with_no_pid_file_and_a_fresh_starting_marker_clears_it()
    {
        DaemonStartingMarker.Write(DaemonRuntime.StartingMarkerFile, DateTimeOffset.UtcNow);

        int exitCode = await DaemonLifecycle.StopAsync(NoAutostart, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        File.Exists(DaemonRuntime.StartingMarkerFile).Should().BeFalse(
            "an explicit stop is the operator's own diagnosis that the spawn the marker describes "
            + "is dead, and must not leave h9k daemon start refusing a new attempt for the rest of "
            + "the marker's grace period");
    }

    [Fact]
    public async Task Stop_with_no_pid_file_and_a_stale_starting_marker_clears_it()
    {
        DaemonStartingMarker.Write(
            DaemonRuntime.StartingMarkerFile,
            DateTimeOffset.UtcNow - DaemonStartingMarker.GracePeriod - TimeSpan.FromSeconds(1));

        int exitCode = await DaemonLifecycle.StopAsync(NoAutostart, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        File.Exists(DaemonRuntime.StartingMarkerFile).Should().BeFalse();
    }
}
