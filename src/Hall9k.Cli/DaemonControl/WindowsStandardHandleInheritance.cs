using System.Runtime.InteropServices;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Strips this process's own stdin/stdout/stderr out of the inheritable set for the scope of a
/// child process launch. <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// on Windows passes <c>bInheritHandles=true</c> to <c>CreateProcess</c> unconditionally, regardless
/// of any <see cref="System.Diagnostics.ProcessStartInfo"/> redirection setting (dotnet/runtime#19569)
/// — every handle this process holds that is marked inheritable, including a caller's own redirected
/// stdout pipe, gets duplicated into the child whether or not the child's <c>ProcessStartInfo</c> asked
/// for any redirection at all. <c>SetHandleInformation</c>'s per-handle inherit flag is the documented
/// way to opt a specific handle out of that regardless of <c>bInheritHandles</c> (Raymond Chen's
/// "Why does my pipe hang" family of posts): clear it before the child is created, restore it after, and
/// the child's own handle table never gets a copy.
/// <para>
/// Origin incident (first Windows install friction log, item 4): <c>DaemonLifecycle.SpawnDetachedWindows</c>'s
/// cmd.exe intermediary stays alive for h9kd's entire run. Without this guard, a caller piping or
/// redirecting <c>h9k daemon start</c>'s own output (a CI step, a PowerShell <c>$output = ...</c>
/// capture) handed cmd.exe a duplicate of that pipe's write handle at creation; h9k itself exited
/// promptly, but the pipe never reached EOF because cmd.exe was still holding it open — a 300s wrapper
/// timeout fired with the daemon already healthy.
/// </para>
/// </summary>
internal static class WindowsStandardHandleInheritance
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Clears the inherit flag on this process's own std handles, restoring each on
    /// <see cref="IDisposable.Dispose"/>. The child process must be created while the returned
    /// scope is still open — restoring before the child exists would defeat the guard.
    /// </summary>
    public static IDisposable SuppressForChildProcesses() =>
        new Scope(Guard(StdInputHandle), Guard(StdOutputHandle), Guard(StdErrorHandle));

    private static uint? Guard(int standardHandle)
    {
        IntPtr handle = GetStdHandle(standardHandle);
        // INVALID_HANDLE_VALUE (-1) or NULL: no real handle to guard (a service host, a
        // console-less parent) — nothing can leak through a handle that does not exist.
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        if (!GetHandleInformation(handle, out uint flags) || (flags & HandleFlagInherit) == 0)
        {
            // Already non-inheritable (or unreadable) — nothing to clear, nothing to restore.
            return null;
        }

        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
        {
            // The handle is still inheritable and a detached child may leak it — the exact
            // hang this guard exists to prevent. Nothing was actually changed, so there is
            // nothing to restore; say so on stderr rather than silently claiming success.
            Console.Error.WriteLine(
                $"h9k: could not suppress inheritance on standard handle {standardHandle} " +
                $"(Win32 error {Marshal.GetLastWin32Error()}); the detached daemon process may inherit it.");
            return null;
        }

        return flags;
    }

    private sealed class Scope(uint? stdin, uint? stdout, uint? stderr) : IDisposable
    {
        public void Dispose()
        {
            Restore(StdInputHandle, stdin);
            Restore(StdOutputHandle, stdout);
            Restore(StdErrorHandle, stderr);
        }

        private static void Restore(int standardHandle, uint? originalFlags)
        {
            if (originalFlags is not { } flags)
            {
                return;
            }

            IntPtr handle = GetStdHandle(standardHandle);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                SetHandleInformation(handle, HandleFlagInherit, flags & HandleFlagInherit);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
