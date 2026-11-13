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
    private const uint CreateNewConsole = 0x00000010;
    private const uint GenericAll = 0x10000000;
    private const int StartFlagUseStandardHandles = 0x00000100;

    /// <summary>The window station a logged-on user's visible desktop lives in; every other station is a service or batch one.</summary>
    private const string InteractiveWindowStation = "WinSta0";

    /// <summary><c>UOI_NAME</c>: the name of a window station or desktop, for <c>GetUserObjectInformation</c>.</summary>
    private const int UserObjectName = 2;

    /// <summary>Git Credential Manager's switch for whether it may open a prompt: <c>never</c> makes it fail instead.</summary>
    internal const string GitCredentialManagerInteractiveVariable = "GCM_INTERACTIVE";

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
            binaryPath, arguments, workingDirectory, logFilePath, environmentOverrides,
            out int processId, out _);
        // The desktop handle Create returned is deliberately never closed here: a desktop is
        // destroyed the moment nothing references it, and h9kd's console host attaches to it a
        // moment AFTER CreateProcess returns, so closing it now could race that attach and leave
        // h9kd with no desktop to open. This process is `h9k daemon start`, which lives for
        // seconds more, and its exit closes the handle long after h9kd's own references exist.
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
            binaryPath, arguments, workingDirectory, logFilePath, environmentOverrides,
            out _, out IntPtr desktop);
        // Held for the daemon's whole run and closed after it, so the desktop's lifetime is
        // never shorter than h9kd's (see StartDetached).
        using IDisposable desktopLifetime = new DesktopLifetime(desktop);
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

        // h9kd's desktop is one nobody looks at, so a Git Credential Manager prompt there would
        // wait for a click that never comes, with git (and the call that started it) wedged
        // behind it. Telling GCM it may never prompt makes a missing credential fail at once
        // instead; the name is GCM's own documented switch. An operator who set it already keeps
        // their value.
        if (!merged.ContainsKey(GitCredentialManagerInteractiveVariable))
        {
            merged[GitCredentialManagerInteractiveVariable] = "never";
        }

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
    /// <strong>The private desktop.</strong> h9kd is started on a desktop created for it in
    /// <c>WinSta0</c> and never switched to, with a new console whose window is visible on that
    /// desktop (<c>CREATE_NEW_CONSOLE</c>, where this used <c>CREATE_NO_WINDOW</c>). Every
    /// process h9kd starts inherits both. Why: while a Remote Desktop session is connected, git
    /// children of a launcher outside the focused app's process tree keep a kernel access token
    /// each (about 4 to 6 a second) when their console is on the visible desktop. Measured on a
    /// root process with its own visible console on a private desktop, git, gh, sh, bash and
    /// the agent CLI kept none (spike 0b4685d2, its <c>tF</c> arm), while a windowless or
    /// hidden console on a private desktop still kept them. Nobody ever sees the window.
    /// The cost is that a child showing UI on that desktop waits for a click nobody can give;
    /// <see cref="EnvironmentBlock"/> turns off the one that matters, Git Credential Manager's
    /// prompt. If the desktop cannot be created, or this process is not in <c>WinSta0</c> (an
    /// OpenSSH session or a non-interactive scheduled task, whose window station is its own), the
    /// old windowless start is used instead of no daemon at all.
    /// </para>
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
        out int processId,
        out IntPtr desktop)
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

        // Assigned inside the try below, once nothing that can still throw stands between the
        // desktop's creation and the CreateProcess that hands it to h9kd.
        desktop = IntPtr.Zero;

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
            // Zero, with a null name, when the desktop cannot be created; the start then keeps the
            // shape it had before the desktop existed (a windowless console on the caller's desktop).
            desktop = CreatePrivateDesktop(out string? desktopName);
            startup.Desktop = desktopName;

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateUnicodeEnvironment | (desktopName is null ? CreateNoWindow : CreateNewConsole),
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
                if (desktop != IntPtr.Zero)
                {
                    CloseDesktop(desktop);
                    desktop = IntPtr.Zero;
                }

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

    /// <summary>
    /// A desktop of its own in the caller's window station, named uniquely so two launchers
    /// never share one. It is only ever referenced by name through
    /// <c>STARTUPINFO.lpDesktop</c>: nothing here switches to it, so the operator's screen never
    /// changes. Returns <see cref="IntPtr.Zero"/> and a null name when the caller's window station
    /// is not <c>WinSta0</c> (a service or batch session, see the comment in the body) or the
    /// call fails (an exhausted desktop heap).
    /// </summary>
    private static IntPtr CreatePrivateDesktop(out string? desktopName)
    {
        // CreateDesktop puts the desktop in THIS process's window station, so the name handed to
        // the child must be that station's, and the private desktop only makes sense in the
        // interactive one: an OpenSSH session or a "run whether the user is logged on or not"
        // scheduled task has a Service-0x0-...$ station of its own, where there is no visible
        // desktop to leak onto and nothing for a console window to be shown on. There the old
        // windowless start is kept rather than naming a desktop the child cannot open.
        if (!string.Equals(CurrentWindowStationName(), InteractiveWindowStation, StringComparison.OrdinalIgnoreCase))
        {
            desktopName = null;
            return IntPtr.Zero;
        }

        string name = $"h9kd-{Environment.ProcessId}-{Guid.NewGuid():N}";
        IntPtr handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        desktopName = handle == IntPtr.Zero ? null : $"{InteractiveWindowStation}\\{name}";
        return handle;
    }

    /// <summary>The name of this process's window station, or an empty string when it cannot be read.</summary>
    private static string CurrentWindowStationName()
    {
        IntPtr station = GetProcessWindowStation();
        if (station == IntPtr.Zero)
        {
            return string.Empty;
        }

        char[] buffer = new char[256];
        return GetUserObjectInformationW(station, UserObjectName, buffer, buffer.Length * sizeof(char), out _)
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    /// <summary>Closes a private desktop handle when disposed; a zero handle is nothing to close.</summary>
    private sealed class DesktopLifetime(IntPtr desktop) : IDisposable
    {
        public void Dispose()
        {
            if (desktop != IntPtr.Zero)
            {
                CloseDesktop(desktop);
            }
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
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Desktop;
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

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(
        string name, IntPtr device, IntPtr deviceMode, uint flags, uint desiredAccess, IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, char[] info, int length, out int needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
