using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Daemon.ProcessManagement;

public sealed class UnixProcessManager : IProcessManager
{
    // Start times can drift slightly between recording and reading; a match within this
    // window means "same process", anything further means the PID was reused.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public bool IsAlive(int processId, DateTimeOffset startedAt) => TryGet(processId, startedAt) is not null;

    public void Terminate(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        if (process is null)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
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
