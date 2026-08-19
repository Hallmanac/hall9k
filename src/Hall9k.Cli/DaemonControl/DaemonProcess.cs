using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

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
