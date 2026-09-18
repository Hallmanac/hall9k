using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Starts h9kd on Windows with its stdout and stderr already pointing at
/// <c>~/.hall9k/h9kd.log</c> through a handle THIS process opens with
/// <c>FILE_APPEND_DATA</c> and the widest share mode Windows offers, then hands to the child
/// as an inheritable standard handle and closes its own copy. Both shipped Windows launch
/// paths go through here — <see cref="DaemonLifecycle"/>'s own detached spawn and the logon
/// autostart task, by way of <c>h9k daemon autostart launch</c>.
/// <para>
/// <strong>Why not cmd.exe's <c>&gt;&gt;</c> redirect, which is what this replaces.</strong>
/// cmd.exe opens an append redirect's target with <c>FILE_SHARE_READ</c> only and holds it for
/// the whole run, so for as long as h9kd lived there was a second writer nobody could ever
/// become: not h9kd's own <c>WindowsAppendOnlyLog</c> takeover, and not
/// <see cref="DaemonLogRotation"/>'s <c>FileAccess.ReadWrite</c> open on
/// <c>LogRotationService</c>'s five-minute tick, which is what actually enforces the log's
/// 8 MB budget while the daemon runs. Measured on Windows 11 Pro 26200, 2026-09-16, against
/// both launch paths as they then shipped: the takeover had never once succeeded on Windows.
/// A handle opened here instead carries <c>FILE_SHARE_READ | FILE_SHARE_WRITE |
/// FILE_SHARE_DELETE</c>, so it refuses nobody, and this process closes it the moment the
/// child exists — leaving h9kd itself the only writer on the log (PLAN.md §16 PLACEHOLDER-d4e64dfa).
/// </para>
/// <para>
/// <strong>Why a handle rather than letting h9kd open the log itself.</strong> h9kd does open
/// its own append handles once it is running (<c>WindowsAppendOnlyLog.TakeOverConsoleOutput</c>,
/// which is what gives its log lines UTF-8 with no byte-order mark rather than the console
/// codepage), but that happens inside <c>Main</c>. A launch that redirects nothing loses every
/// line written before <c>Main</c> is ever reached — a missing .NET runtime, an assembly that
/// will not load — which on the autostart path is exactly the failure nobody is watching for.
/// The inherited handle catches those the same way cmd.exe's redirect did.
/// </para>
/// <para>
/// <strong>Why <c>CreateProcess</c> directly.</strong> There is no way to hand a child an
/// arbitrary standard handle through <see cref="System.Diagnostics.ProcessStartInfo"/>: its
/// redirection options only ever give the child a pipe THIS process owns, which is the
/// opposite of what is wanted here (the point is a handle whose lifetime is the child's, not
/// this command's). <c>CreateProcessW</c> with <c>STARTF_USESTDHANDLES</c> is the documented
/// way, and it is what <see cref="System.Diagnostics.Process"/> itself calls underneath.
/// </para>
/// </summary>
internal static class WindowsDaemonLaunch
{
    private const uint FileAppendData = 0x0004;
    private const uint GenericRead = 0x80000000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartFlagUseStandardHandles = 0x00000100;

    private const uint WaitObjectZero = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    /// <summary>
    /// How long a wait on the daemon blocks before looking at its cancellation token again.
    /// Only <see cref="RunUntilExitAsync"/> waits at all, and it waits for the daemon's whole
    /// life, so this is a responsiveness floor for a Ctrl-C rather than a poll of anything:
    /// the handle wait itself is what actually observes the exit.
    /// </summary>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Launch h9kd and return its process id without waiting for it. An orphaned Windows
    /// process outlives the process that started it with no reparenting step required, unlike
    /// the Unix side's double fork, so this is the whole of the detach —
    /// <see cref="DaemonLifecycle"/>'s own start path calls this and returns while h9kd boots.
    /// </summary>
    public static int StartDetached(
        string binaryPath,
        string workingDirectory,
        string logFilePath,
        IReadOnlyList<KeyValuePair<string, string>> environmentOverrides,
        string arguments = "")
    {
        using SafeProcessHandle daemon = Create(
            binaryPath, arguments, workingDirectory, logFilePath, environmentOverrides, out int processId);
        return processId;
    }

    /// <summary>
    /// Launch h9kd, wait for it, and answer with its own exit code. This is the shape the logon
    /// autostart task needs: its action chain (wscript.exe to cmd.exe to here) has to stay alive
    /// for the daemon's whole run, because Task Scheduler's own <c>RestartOnFailure</c> reads the
    /// action's exit code, and because a task that reports <c>Running</c> only while its daemon
    /// runs is what <c>WindowsDaemonAutostart.DisableAsync</c> uses to tell a daemon the task
    /// started from one an operator started by hand.
    /// <para>
    /// A cancelled token ends the wait, never the daemon: the daemon is detached by design and
    /// a Ctrl-C delivered to a launcher is not an instruction to stop it. The exit code in that
    /// case is <c>0</c>, which leaves an autostart task Ready with its daemon still running —
    /// exactly the posture an <c>h9k daemon start</c> would have left behind.
    /// </para>
    /// </summary>
    public static async Task<int> RunUntilExitAsync(
        string binaryPath,
        string workingDirectory,
        string logFilePath,
        IReadOnlyList<KeyValuePair<string, string>> environmentOverrides,
        CancellationToken cancellationToken,
        string arguments = "")
    {
        using SafeProcessHandle daemon = Create(
            binaryPath, arguments, workingDirectory, logFilePath, environmentOverrides, out _);
        while (!cancellationToken.IsCancellationRequested)
        {
            uint waited = await Task.Run(
                () => WaitForSingleObject(daemon, (uint)WaitSlice.TotalMilliseconds), CancellationToken.None);
            if (waited == WaitObjectZero)
            {
                return GetExitCodeProcess(daemon, out uint exitCode)
                    ? unchecked((int)exitCode)
                    : throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess on h9kd failed.");
            }

            if (waited != WaitTimeout)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting on h9kd failed.");
            }
        }

        return 0;
    }

    /// <summary>
    /// The environment h9kd is created with: this process's own, plus
    /// <paramref name="overrides"/>, plus the marker that tells h9kd its stdout is the launcher's
    /// append handle rather than a console of its own (see
    /// <see cref="DaemonRuntime.AppendOnlyLogEnvironmentVariable"/>). Set here rather than by
    /// each caller so the two launch paths cannot diverge on it — and never by leaving it in
    /// THIS process's environment, which would forward it to every other child this command
    /// happens to spawn. Internal for direct unit coverage: an environment block is a flat,
    /// null-separated, sorted <c>NAME=VALUE</c> string, which is worth asserting without
    /// creating a process.
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyList<KeyValuePair<string, string>> overrides)
    {
        // Ordinal-ignore-case throughout: Windows environment names are case-insensitive, so an
        // override named `Path` must replace an inherited `PATH` rather than sit beside it as a
        // second entry the child would resolve arbitrarily.
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry inherited in Environment.GetEnvironmentVariables())
        {
            merged[(string)inherited.Key] = (string?)inherited.Value ?? string.Empty;
        }

        foreach ((string name, string value) in overrides)
        {
            merged[name] = value;
        }

        merged[DaemonRuntime.AppendOnlyLogEnvironmentVariable] = "1";

        StringBuilder block = new();
        foreach ((string name, string value) in merged.OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    /// <summary>
    /// Opens <paramref name="logFilePath"/> the way h9kd's own
    /// <c>WindowsAppendOnlyLog</c> opens it, with one addition: the handle is inheritable, so
    /// <c>CreateProcess</c> can hand it to the child as a standard handle.
    /// <c>FILE_APPEND_DATA</c> alone (never combined with <c>FILE_WRITE_DATA</c> or
    /// <c>GENERIC_WRITE</c>) is the one Win32 access mask that re-resolves end-of-file on every
    /// write, the way a POSIX <c>O_APPEND</c> descriptor does — which is the precondition
    /// <see cref="DaemonLogRotation"/>'s copy-then-truncate depends on, and precisely what a
    /// cmd.exe <c>&gt;&gt;</c> handle (write position cached at open time) does not give.
    /// Internal so a test can hold the launcher's own handle shape while asserting what h9kd
    /// can still do underneath it.
    /// </summary>
    internal static SafeFileHandle OpenInheritableAppendLog(string logFilePath)
    {
        // A first-ever autostart launch reaches here before anything else has had a reason to
        // create ~/.hall9k, and CreateFile does not create directories.
        if (Path.GetDirectoryName(Path.GetFullPath(logFilePath)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        return Open(logFilePath, FileAppendData | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, OpenAlways);
    }

    /// <summary>
    /// Opens the log and NUL, creates the process with both as its standard handles, and closes
    /// this process's own copies before returning the process handle.
    /// <para>
    /// <paramref name="arguments"/> is empty for both shipped callers — h9kd takes none — and
    /// exists so this launcher can be driven against a real process with a chosen exit code and
    /// a chosen line of output, which is the only way to prove the handle handoff and the exit
    /// code relay actually work (<c>WindowsDaemonLaunchTests</c>). It is appended raw: a caller
    /// that ever passes one owns its quoting, the same contract
    /// <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> has.
    /// </para>
    /// </summary>
    private static SafeProcessHandle Create(
        string binaryPath,
        string arguments,
        string workingDirectory,
        string logFilePath,
        IReadOnlyList<KeyValuePair<string, string>> environmentOverrides,
        out int processId)
    {
        // Disposed before this method returns, whichever way it returns: the whole point is that
        // the only handles left on the log once the child exists are the child's own. A launcher
        // still holding one would put back the second writer this type exists to remove — and on
        // the autostart path this process lives for the daemon's entire run, so "still holding"
        // would mean weeks.
        using SafeFileHandle log = OpenInheritableAppendLog(logFilePath);
        // h9kd reads nothing from stdin; NUL is what cmd.exe's own `< NUL` supplied, and a child
        // with no stdin handle at all is a different thing from one whose stdin is at end of file.
        using SafeFileHandle nul = Open("NUL", GenericRead | Synchronize, FileShareRead | FileShareWrite, OpenExisting);

        StartupInfo startup = new()
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartFlagUseStandardHandles,
            StandardInput = nul.DangerousGetHandle(),
            StandardOutput = log.DangerousGetHandle(),
            StandardError = log.DangerousGetHandle(),
        };

        // CreateProcessW writes into its lpCommandLine buffer, so it cannot be a literal: a
        // StringBuilder is what System.Diagnostics.Process itself passes for the same reason.
        StringBuilder commandLine = new(
            arguments.Length == 0 ? $"\"{binaryPath}\"" : $"\"{binaryPath}\" {arguments}");

        // The same guard the cmd.exe spawn this replaces needed, for the same reason and now
        // with a longer-lived child to leak into: CreateProcess is called with
        // bInheritHandles=true (STARTF_USESTDHANDLES requires it), which duplicates EVERY
        // inheritable handle this process holds into h9kd, not just the two named above. A
        // caller piping this command's own output would otherwise hand h9kd its pipe's write
        // handle and never see end-of-file until the daemon itself exits.
        using IDisposable handleGuard = WindowsStandardHandleInheritance.SuppressForChildProcesses();

        // Pinned rather than marshalled to unmanaged memory, so the only cleanup this owes is
        // the Free() below — taken last, immediately before the try that frees it, so nothing
        // can throw between the two.
        char[] environment = EnvironmentBlock(environmentOverrides).ToCharArray();
        GCHandle pinnedEnvironment = GCHandle.Alloc(environment, GCHandleType.Pinned);
        try
        {
            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    pinnedEnvironment.AddrOfPinnedObject(),
                    workingDirectory,
                    ref startup,
                    out ProcessInformation created))
            {
                // The code AND the OS's own text for it, because the two failures a reader
                // actually meets here are error 2 (the binary is not where the caller said) and
                // error 193 (it is there but is not an executable image), and the number alone
                // sends them looking. Nothing about the daemon's own state is claimed in this
                // message: every caller frames that itself, and saying it here too read as a
                // stutter when the path was actually run (self-review round two).
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error, $"CreateProcess({binaryPath}) failed with Win32 error {error} ({new Win32Exception(error).Message})");
            }

            // Nothing ever resumes or waits on the daemon's primary thread; the process handle is
            // the only one worth keeping, and an unclosed thread handle would pin its kernel
            // object for this launcher's whole (possibly weeks-long) life.
            CloseHandle(created.Thread);
            processId = created.ProcessId;
            return new SafeProcessHandle(created.Process, ownsHandle: true);
        }
        finally
        {
            pinnedEnvironment.Free();
        }
    }

    private static SafeFileHandle Open(string path, uint desiredAccess, uint shareMode, uint creationDisposition)
    {
        SecurityAttributes inheritable = new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            SecurityDescriptor = IntPtr.Zero,
            InheritHandle = 1,
        };

        SafeFileHandle handle = CreateFileW(
            path, desiredAccess, shareMode, ref inheritable, creationDisposition, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            // The raw error number is the useful part and the two ways this realistically fails
            // read very differently from it: 5 on the log is a permissions problem on ~/.hall9k,
            // and 32 is somebody holding the log with a share mode that excludes writers — a
            // backup pass or a scanner, never the launcher itself any more.
            //
            // Not retried, unlike h9kd's own takeover of the same file: a failure here means no
            // daemon at all, which is exactly what the cmd.exe redirect this replaces did with
            // the same failure (cmd.exe reported it and never ran h9kd), so this is the shipped
            // behaviour rather than a new fragility. What it owes over the redirect is saying so
            // — both callers catch this and name the likely holder rather than letting an
            // IOException reach a stack trace.
            throw new IOException($"CreateFile({path}) failed with Win32 error {error}.");
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>
    /// <c>STARTUPINFOW</c>, field for field and in order — a sequential layout is only correct
    /// if it is complete, so every reserved and unused member is present rather than skipped.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
