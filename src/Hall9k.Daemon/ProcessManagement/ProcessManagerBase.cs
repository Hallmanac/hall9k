using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// IsAlive and Terminate have no platform-specific content: <see cref="Process.GetProcessById"/>,
/// <see cref="Process.StartTime"/>, and <c>Kill(entireProcessTree: true)</c> already behave
/// identically on every OS .NET targets here, and the pid-reuse check that tells "same
/// process" from "the pid was recycled" (Decisions Log #2) is a comparison, not a syscall —
/// nothing about it differs by platform. Spawn is the one place a real difference exists (the
/// native shell that gives the child its own file handle), so it is the only member each
/// concrete implementation supplies.
/// </summary>
public abstract class ProcessManagerBase : IProcessManager
{
    // Start times can drift slightly between recording and reading; a match within this
    // window means "same process", anything further means the PID was reused.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public abstract SpawnedProcess Spawn(ProcessSpawnRequest request);

    /// <summary>
    /// Reads a just-started process's start time, tolerating the same race
    /// <see cref="TryGet"/> already tolerates on the read side: a command that exits before
    /// this runs (an <c>exec</c>'d one-liner, or cmd.exe racing a trivial child) leaves the OS
    /// with nothing left to report. <see cref="DateTimeOffset.MinValue"/> is recorded instead
    /// of a plausible-looking guess — AGENTS.md's "never guess at unobserved facts" applies
    /// directly here, since this value becomes the process identity <see cref="TryGet"/> and
    /// every later liveness check key off. Stamping "now" would risk a false match if the OS
    /// recycled the pid within <see cref="StartTimeTolerance"/> before the next read; the
    /// sentinel instead guarantees <see cref="TryGet"/> never matches a real process's start
    /// time, so a process that was already gone at spawn time stays reported as gone.
    /// </summary>
    protected static DateTimeOffset ReadStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        return process is not null;
    }

    public void Terminate(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        process?.Kill(entireProcessTree: true);
    }

    private static Process? TryGet(int processId, DateTimeOffset startedAt)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            DateTimeOffset actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if ((actualStart - startedAt).Duration() > StartTimeTolerance)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Win32Exception: StartTime is unreadable because the pid now belongs to
            // another user's (often privileged) process — nothing the daemon spawned,
            // so the recorded process is gone.
            return null;
        }
    }
}
