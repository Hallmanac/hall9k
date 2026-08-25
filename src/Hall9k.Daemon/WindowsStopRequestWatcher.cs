using System.Diagnostics;
using System.Text.Json;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;

namespace Hall9k.Daemon;

/// <summary>
/// Windows has no SIGTERM a CLI can send to an arbitrary process, so <c>h9k daemon stop</c>
/// asks gracefully by writing <see cref="DaemonRuntime.StopRequestFile"/> instead, and this
/// is what watches for it — the same doorbell-plus-poll-backstop idiom every other daemon
/// loop here already uses (<c>DispatchLoop</c>, <c>CardPublicationLoop</c>), just with the
/// filesystem standing in for the doorbell since there is no domain event to LISTEN for
/// here. Registered only on Windows (see <c>Program.cs</c>); the request file is never
/// written anywhere on Unix, which keeps using a real SIGTERM.
/// </summary>
public sealed class WindowsStopRequestWatcher(
    IHostApplicationLifetime lifetime, ILogger<WindowsStopRequestWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Start times drift slightly between recording and reading; within this window is
    // "same process", beyond it the pid was reused — mirrors DaemonProcess.IsAlive's own
    // tolerance in Hall9k.Cli (not referenced from here: Daemon does not depend on Cli).
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    // Same JsonSerializerOptions shape DaemonPidFile writes with, so the descriptor this
    // reads back round-trips regardless of property casing.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DateTimeOffset startedAt = CurrentProcessStartedAt();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval);
        do
        {
            if (!IsOwnStopRequest())
            {
                continue;
            }

            logger.LogInformation(
                "Graceful stop requested via {StopRequestFile} — shutting down", DaemonRuntime.StopRequestFile);
            DeleteRequestFile();
            lifetime.StopApplication();
            return;
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    /// <summary>
    /// True only when the file both exists and names THIS process's identity — pid AND
    /// start time, never a bare pid (Decisions Log #2: the same discipline
    /// <see cref="DaemonRuntime.PidFile"/> already carries). h9k daemon stop and
    /// WindowsDaemonAutostart.DisableAsync both write the identity of the daemon they mean
    /// to stop, so a mismatch means the file is stale — left behind by a reboot mid-wait, a
    /// force-kill from Task Manager, or a delete that failed against a lock — and belongs
    /// to a daemon that is already gone, never to this one. A bare pid match alone is not
    /// enough: the next daemon this machine starts can be assigned the very same pid, and
    /// honoring it anyway would shut it down seconds after a clean-looking h9k daemon
    /// start. A mismatch is cleared here rather than left for the next daemon to hit the
    /// same trap.
    /// </summary>
    private bool IsOwnStopRequest()
    {
        string content;
        try
        {
            content = File.ReadAllText(DaemonRuntime.StopRequestFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // IOException covers both "no file yet" (FileNotFoundException/
            // DirectoryNotFoundException are IOException subtypes) and a sharing violation
            // against a write still in flight; UnauthorizedAccessException covers the path
            // existing but being unreadable (a directory in its place, a denying ACL) —
            // matching DeleteRequestFile's own guard below for the same path, so this
            // BackgroundService never lets an escaping exception take the whole host down
            // via the default StopHost behavior. Nothing to act on yet either way; the next
            // tick tries again.
            return false;
        }

        DaemonProcessDescriptor? requested;
        try
        {
            requested = JsonSerializer.Deserialize<DaemonProcessDescriptor>(content, SerializerOptions);
        }
        catch (JsonException)
        {
            requested = null;
        }

        if (requested is not null
            && requested.ProcessId == Environment.ProcessId
            && (requested.StartedAt - startedAt).Duration() <= StartTimeTolerance)
        {
            return true;
        }

        logger.LogWarning(
            "Ignoring stale {StopRequestFile} ({Content}) — not this daemon (pid {ProcessId}, started {StartedAt:u})",
            DaemonRuntime.StopRequestFile, content.Trim(), Environment.ProcessId, startedAt);
        DeleteRequestFile();
        return false;
    }

    /// <summary>
    /// Deleted before <see cref="IHostApplicationLifetime.StopApplication"/> runs, not after:
    /// a leftover request file would otherwise stop the very next daemon this machine
    /// starts the moment its own watcher's first tick sees it. A delete that itself fails
    /// (locked, permissions) still lets the caller proceed — a stale copy on disk, but
    /// whatever it asked for (this daemon stopping, or the mismatch being noticed) already
    /// happened.
    /// </summary>
    private void DeleteRequestFile()
    {
        try
        {
            File.Delete(DaemonRuntime.StopRequestFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not delete {StopRequestFile} — a stale copy could stop the next daemon that starts",
                DaemonRuntime.StopRequestFile);
        }
    }

    private static DateTimeOffset CurrentProcessStartedAt() =>
        new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
