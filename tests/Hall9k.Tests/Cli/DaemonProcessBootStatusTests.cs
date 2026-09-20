using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// DaemonProcess.ProbeBootStatus reads both DaemonRuntime.PidFile and
/// DaemonRuntime.StartingMarkerFile, both under RunPaths.Root — redirecting HALL9K_HOME to
/// a temp directory keeps this off a developer's real ~/.hall9k.
/// </summary>
public sealed class DaemonProcessBootStatusTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    private string _home => _scopedHome.Home;

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public void No_pid_file_and_no_marker_reads_as_not_running()
    {
        DaemonProcess.ProbeBootStatus().Should().Be(new DaemonBootStatus(DaemonBootState.NotRunning, null));
    }

    [Fact]
    public void A_fresh_starting_marker_with_no_pid_file_reads_as_starting()
    {
        DaemonStartingMarker.Write(DaemonRuntime.StartingMarkerFile, DateTimeOffset.UtcNow);

        DaemonProcess.ProbeBootStatus().State.Should().Be(DaemonBootState.Starting,
            "a launch spawned moments ago, still short of its own pid file, must not read the same as down "
            + "(task 92da629d — the Arx Windows node's ~15s pre-guard boot)");
    }

    [Fact]
    public void A_stale_starting_marker_with_no_pid_file_reads_as_not_running()
    {
        DaemonStartingMarker.Write(
            DaemonRuntime.StartingMarkerFile,
            DateTimeOffset.UtcNow - DaemonStartingMarker.GracePeriod - TimeSpan.FromSeconds(1));

        DaemonProcess.ProbeBootStatus().State.Should().Be(DaemonBootState.NotRunning,
            "a spawn that never produced a pid file within the grace period is not still starting");
    }

    [Fact]
    public void A_confirmed_pid_file_reads_as_running_even_with_a_starting_marker_present()
    {
        using Process current = Process.GetCurrentProcess();
        DaemonProcessDescriptor descriptor = new(
            current.Id, new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero));
        DaemonPidFile.Write(DaemonRuntime.PidFile, descriptor);
        DaemonStartingMarker.Write(DaemonRuntime.StartingMarkerFile, DateTimeOffset.UtcNow);

        DaemonProcess.ProbeBootStatus().Should().Be(new DaemonBootStatus(DaemonBootState.Running, descriptor),
            "a confirmed pid file is the stronger fact and wins over a marker left behind from the same launch");
    }
}
