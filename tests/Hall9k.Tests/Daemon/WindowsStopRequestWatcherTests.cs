using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// HALL9K_HOME is redirected to a temp directory so the stop-request file this writes
/// never touches a developer's or CI runner's real home — same discipline as
/// UpdateCommandTests, and the same collection serializes this against every other
/// HALL9K_HOME redirect.
/// </summary>
[Collection("Hall9kHome")]
public sealed class WindowsStopRequestWatcherTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-stop-watcher-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public WindowsStopRequestWatcherTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        try
        {
            Directory.Delete(home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_stop_request_naming_a_different_pid_is_ignored_and_cleared()
    {
        // A pid that is not this test process's own — stands in for a request written for
        // a daemon that already died some other way (a reboot mid-wait, a force-kill), left
        // behind for whichever daemon starts next.
        int otherProcessId = Environment.ProcessId == 1 ? 2 : 1;
        await DaemonPidFile.WriteAsync(
            DaemonRuntime.StopRequestFile, new DaemonProcessDescriptor(otherProcessId, DateTimeOffset.UtcNow), CancellationToken.None);

        FakeApplicationLifetime lifetime = new();
        WindowsStopRequestWatcher watcher = new(lifetime, NullLogger<WindowsStopRequestWatcher>.Instance);

        await RunBrieflyAsync(watcher);

        lifetime.StopRequested.Should().BeFalse(
            "the file names a different daemon's pid, so this one has nothing to act on");
        File.Exists(DaemonRuntime.StopRequestFile).Should().BeFalse(
            "the stale file is cleared here so it cannot also stop the next daemon that starts");
    }

    [Fact]
    public async Task A_stop_request_naming_this_pid_but_a_different_start_time_is_ignored_and_cleared()
    {
        // Same pid, a start time far outside the tolerance — stands in for a leftover
        // request from a daemon that died before its watcher could see it (a force-kill, a
        // reboot mid-wait), and whose pid a later, unrelated daemon on this machine happens
        // to reuse. A bare-pid match alone would honor this and shut the new daemon down
        // seconds after a clean-looking start; the start time is what tells them apart.
        await DaemonPidFile.WriteAsync(
            DaemonRuntime.StopRequestFile,
            new DaemonProcessDescriptor(Environment.ProcessId, DateTimeOffset.UtcNow - TimeSpan.FromHours(1)),
            CancellationToken.None);

        FakeApplicationLifetime lifetime = new();
        WindowsStopRequestWatcher watcher = new(lifetime, NullLogger<WindowsStopRequestWatcher>.Instance);

        await RunBrieflyAsync(watcher);

        lifetime.StopRequested.Should().BeFalse(
            "the pid matches by reuse but the start time does not, so this is not the daemon the request meant");
        File.Exists(DaemonRuntime.StopRequestFile).Should().BeFalse(
            "the stale file is cleared here so it cannot also stop the next daemon that starts");
    }

    [Fact]
    public async Task A_stop_request_naming_this_process_is_honored()
    {
        DateTimeOffset ownStartedAt = new(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        await DaemonPidFile.WriteAsync(
            DaemonRuntime.StopRequestFile, new DaemonProcessDescriptor(Environment.ProcessId, ownStartedAt), CancellationToken.None);

        FakeApplicationLifetime lifetime = new();
        WindowsStopRequestWatcher watcher = new(lifetime, NullLogger<WindowsStopRequestWatcher>.Instance);

        await RunBrieflyAsync(watcher);

        lifetime.StopRequested.Should().BeTrue();
        File.Exists(DaemonRuntime.StopRequestFile).Should().BeFalse();
    }

    [Fact]
    public void A_stale_stop_request_with_unchanged_content_is_warned_about_only_once()
    {
        // Stands in for a delete that keeps failing against the same stale file (the origin
        // finding: h9kd.stop carrying the read-only attribute) — here simulated by simply
        // rewriting the identical content back before the next poll, since IsOwnStopRequest
        // does not know or care whether the delete actually succeeded, only whether the
        // content it just read matches what it already warned about.
        string staleContent = """{"ProcessId":999999,"StartedAt":"2024-01-01T00:00:00Z"}""";
        ListLogger<WindowsStopRequestWatcher> logger = new();
        WindowsStopRequestWatcher watcher = new(new FakeApplicationLifetime(), logger);

        File.WriteAllText(DaemonRuntime.StopRequestFile, staleContent);
        watcher.IsOwnStopRequest().Should().BeFalse();

        File.WriteAllText(DaemonRuntime.StopRequestFile, staleContent);
        watcher.IsOwnStopRequest().Should().BeFalse();

        logger.Lines.Count(line => line.Contains("Ignoring stale")).Should().Be(
            1, "a stale file that will not go away must not re-flood the log on every 250ms tick");
    }

    [Fact]
    public void A_stale_stop_request_naming_a_different_daemon_gets_its_own_fresh_warning()
    {
        ListLogger<WindowsStopRequestWatcher> logger = new();
        WindowsStopRequestWatcher watcher = new(new FakeApplicationLifetime(), logger);

        File.WriteAllText(DaemonRuntime.StopRequestFile, """{"ProcessId":999999,"StartedAt":"2024-01-01T00:00:00Z"}""");
        watcher.IsOwnStopRequest().Should().BeFalse();

        File.WriteAllText(DaemonRuntime.StopRequestFile, """{"ProcessId":888888,"StartedAt":"2024-01-02T00:00:00Z"}""");
        watcher.IsOwnStopRequest().Should().BeFalse();

        logger.Lines.Count(line => line.Contains("Ignoring stale")).Should().Be(
            2, "distinct stale content is a distinct occurrence, and each still gets its own warning");
    }

    private static async Task RunBrieflyAsync(WindowsStopRequestWatcher watcher)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await watcher.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }
}
