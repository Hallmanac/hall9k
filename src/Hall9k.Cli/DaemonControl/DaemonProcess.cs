using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The three states <see cref="DaemonProcess.ProbeBootStatus"/> distinguishes: a confirmed
/// pid file, no daemon and no launch in flight, or a recent <c>h9k daemon start</c> spawn
/// still booting. <see cref="Starting"/> exists so that window never reads the same as
/// <see cref="NotRunning"/> — the read that used to invite a second launch straight into
/// the first spawn's singleton lock.
/// </summary>
public enum DaemonBootState
{
    NotRunning,
    Starting,
    Running,
}

/// <summary>
/// <see cref="Running"/> is set only when <see cref="State"/> is <see cref="DaemonBootState.Running"/>.
/// </summary>
public sealed record DaemonBootStatus(DaemonBootState State, DaemonProcessDescriptor? Running);

/// <summary>
/// How the CLI knows whether h9kd is running: the pid file's recorded identity
/// (pid + process start time, Decisions Log #2 — a bare pid is a lie waiting to
/// happen) verified against the live process table. A stale pid file reads as
/// "not running", never as an error.
/// </summary>
public static class DaemonProcess
{
    // Start times drift slightly between recording and reading; within this window is
    // "same process", beyond it the pid was reused (mirrors UnixProcessManager).
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public static DaemonProcessDescriptor? Probe()
    {
        DaemonProcessDescriptor? recorded = DaemonPidFile.TryRead(DaemonRuntime.PidFile);
        return recorded is not null && IsAlive(recorded.ProcessId, recorded.StartedAt)
            ? recorded
            : null;
    }

    /// <summary>
    /// <see cref="Probe"/>, widened to distinguish "not running" from "a launch h9k daemon
    /// start just kicked off is still booting" — the pid file alone cannot tell those apart
    /// for however long the daemon takes to reach its own single-instance guard and write
    /// it, which has taken up to ~15s on at least one real Windows machine (task 92da629d).
    /// A stale or absent <see cref="DaemonStartingMarker"/> reads as
    /// <see cref="DaemonBootState.NotRunning"/>, never as an error.
    /// </summary>
    public static DaemonBootStatus ProbeBootStatus()
    {
        DaemonProcessDescriptor? running = Probe();
        if (running is not null)
        {
            return new DaemonBootStatus(DaemonBootState.Running, running);
        }

        bool starting = DaemonStartingMarker.IndicatesRecentLaunch(DaemonRuntime.StartingMarkerFile, DateTimeOffset.UtcNow);
        return new DaemonBootStatus(starting ? DaemonBootState.Starting : DaemonBootState.NotRunning, null);
    }

    /// <summary>
    /// The pid exists at all. Only for watching a process whose identity was
    /// established moments earlier disappear (autostart disable watching the pid
    /// launchd reported for the job) — over a few seconds pid reuse is not a real
    /// risk, and the answer is only ever used to say "gone" or "still going".
    /// Everything else asks <see cref="IsAlive"/>, which checks identity too.
    /// </summary>
    public static bool Exists(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            DateTimeOffset actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (actualStart - startedAt).Duration() <= StartTimeTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Win32Exception: StartTime is unreadable because the pid now belongs to
            // another user's (often privileged) process — h9kd runs as this user, so
            // that pid cannot be our daemon. Stale, not an error.
            return false;
        }
    }
}
