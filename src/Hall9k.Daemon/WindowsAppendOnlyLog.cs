using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon;

/// <summary>
/// Replaces the process's inherited stdout/stderr with handles this process opens itself,
/// using Windows's <c>FILE_APPEND_DATA</c>-only access right — the one Win32 access mask
/// that, like a POSIX <c>O_APPEND</c> descriptor, re-resolves to the file's current
/// end-of-file on every write rather than writing at a position cached when the handle was
/// opened.
/// <para>
/// <see cref="DaemonLogRotation"/> truncates the log in place through a second handle while
/// this process keeps writing through its own, and that is only safe when the writer's
/// handle re-resolves end-of-file per write (its own doc comment names this as the
/// precondition, true of the CLI's <c>&gt;&gt;</c> redirect and launchd's StandardOutPath on
/// Unix, both real <c>O_APPEND</c>). A cmd.exe <c>&gt;&gt;</c> handle does not have that
/// property: its write position is a value cached at open time, so a rotation that truncates
/// the file out from under it leaves the next write landing at the old (now past-end-of-file)
/// offset, which Windows answers by zero-filling the gap. The log would then read back at its
/// pre-rotation size, padded with NULs, and the budget <see cref="DaemonLogRotation"/> exists
/// to enforce would never actually hold. Opening a fresh handle with only
/// <c>FILE_APPEND_DATA</c> (never combined with <c>FILE_WRITE_DATA</c>/<c>GENERIC_WRITE</c>)
/// gets the same self-healing-across-truncation property real <c>O_APPEND</c> gives the Unix
/// side, so this process's own writes survive a rotation regardless of who performs it.
/// </para>
/// <para>
/// <strong>The share mode is deliberately the widest one Windows offers</strong>
/// (<c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>), because an operator's
/// window follows this log while the daemon writes it — the orchestrator recipe's own
/// start-up step arms a <c>Get-Content -Wait</c> (or <c>tail -F</c>) reader on it. Anything
/// narrower would make that reader and this writer mutually exclusive, and the reader is the
/// one Windows would refuse.
/// </para>
/// <para>
/// <strong>What still gets inherited, and why this takeover is worth doing anyway.</strong> The
/// handle h9kd inherits as stdout on Windows is now itself a <c>FILE_APPEND_DATA</c> handle its
/// launcher opened with that same permissive share mode, and closed its own copy of
/// (<c>WindowsDaemonLaunch</c> in Hall9k.Cli, reached by <c>DaemonLifecycle.SpawnDetachedWindows</c>
/// and by <c>h9k daemon autostart launch</c> — PLAN.md §16 PLACEHOLDER-d4e64dfa). So the inherited handle would
/// survive a rotation on its own, and what this adds on top of it is the encoding: .NET writes a
/// redirected <see cref="Console"/> in the console code page, where these writers are UTF-8 with
/// no byte-order mark, which is what the log has always been read back as.
/// </para>
/// <para>
/// <strong>What a sharing violation on this open means, then.</strong> Windows decides a second
/// open by checking the requested access against every existing handle's share mode, so this
/// open is refused only when somebody holds the log with a share mode that excludes writers —
/// and after §16 PLACEHOLDER-d4e64dfa that somebody is never h9kd's own launcher. It is a backup agent, an
/// on-access virus scanner, an editor left open on the log: all transient, all worth waiting a
/// few bounded seconds for, which is what the retry does. The one structural holder left is a
/// Windows node whose autostart registration predates §16 PLACEHOLDER-d4e64dfa and still carries the old
/// <c>cmd.exe /c "h9kd &lt; NUL &gt;&gt; h9kd.log 2&gt;&amp;1"</c> launch script (no install or
/// update rewrites it; only <c>h9k daemon autostart enable</c> does), so the give-up message
/// checks the one part of that it can actually observe — whether this process's own inherited
/// stdout or stderr targets the log — and names that remedy when it does.
/// </para>
/// <para>
/// <strong>What losing this handle costs.</strong> The encoding above, and nothing else: the
/// inherited handle is a handle onto this very same <c>h9kd.log</c>, so every line still lands
/// there, every reader following it still sees them, and <see cref="DaemonLogRotation"/>'s own
/// truncation still lands underneath it. On a legacy cmd.exe-launched node it costs more — that
/// launcher's share mode refuses the rotation's <c>FileAccess.ReadWrite</c> open just as flatly
/// as it refuses this one (measured on Windows 11 Pro 26200, 2026-09-16), so
/// <c>LogRotationService</c>'s five-minute tick logs "Log rotation failed; will retry next tick"
/// and the 8 MB budget goes unenforced until something rotates the log with nothing holding it.
/// </para>
/// <para>
/// Every sharing-violation give-up path names the holder through
/// <see cref="WindowsFileLockHolders"/> rather than reporting a bare Win32 error 32: the two
/// reports on mailbox issue #1 (the Windows project window at the v0.4.0 restart, 2026-09-08
/// 00:28 EDT, and the Windows node window after the v0.5.1 install, 2026-09-09 09:40 EDT) each
/// had to reason backwards from the error number alone, and each landed on the wrong holder.
/// </para>
/// </summary>
public static class WindowsAppendOnlyLog
{
    private const uint FileAppendData = 0x0004;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint FileTypeDisk = 0x0001;
    private const uint VolumeNameDos = 0x0000;
    private const int FinalPathCharacters = 1024;

    /// <summary>
    /// How long a sharing violation is retried before the fallback. Short on purpose: this
    /// runs before the daemon has logged its first line, so every millisecond spent here is a
    /// millisecond an operator's window sees nothing, and the holders worth waiting out let go
    /// in well under this.
    /// </summary>
    internal static readonly TimeSpan DefaultRetryBudget = TimeSpan.FromSeconds(3);

    /// <summary>The pause between retries; the budget divided by this is the attempt count.</summary>
    internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(150);

    // Encoding.UTF8 carries a byte-order-mark preamble that StreamWriter only suppresses
    // when the underlying stream already reports Position > 0 — never true for a freshly
    // opened FILE_APPEND_DATA handle, even though it writes at end-of-file, so each of the
    // two writers TakeOverConsoleOutput opens would otherwise stamp EF BB BF into the middle
    // of an already-populated h9kd.log on every daemon start. The cmd.exe `>>` handle this
    // replaces never emitted one, so this is that same no-BOM behavior, made explicit.
    private static readonly UTF8Encoding NoPreambleUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Swaps <see cref="Console.Out"/> and <see cref="Console.Error"/> for two independent
    /// append-only handles onto <paramref name="logFilePath"/>. Called once, early in
    /// Program.cs before the console logger provider is built, so every line the daemon
    /// ever logs — not just the ones after the first rotation — goes through a handle
    /// rotation-truncation cannot corrupt.
    /// <para>
    /// Both handles are opened before either one is installed, so a failure on the second
    /// leaves the process wholly on its inherited handles rather than split across the two
    /// mechanisms — Program.cs's fallback line describes one state, and this is what makes
    /// that description true.
    /// </para>
    /// </summary>
    public static void TakeOverConsoleOutput(string logFilePath)
    {
        StreamWriter output = OpenAppendWriter(logFilePath);
        StreamWriter error;
        try
        {
            error = OpenAppendWriter(logFilePath);
        }
        catch
        {
            output.Dispose();
            throw;
        }

        Console.SetOut(output);
        Console.SetError(error);
    }

    /// <summary>Internal for direct unit coverage against a real truncate race, the same way <c>WindowsDaemonAutostart.RecordedVariableNames</c> is tested without going through a live schtasks registration.</summary>
    internal static StreamWriter OpenAppendWriter(string logFilePath) =>
        OpenAppendWriter(logFilePath, DefaultRetryBudget, DefaultRetryDelay, Thread.Sleep);

    /// <summary>
    /// The retry is expressed as a budget plus a delay rather than a wall-clock deadline so a
    /// test can drive it deterministically: <paramref name="sleep"/> stands in for
    /// <see cref="Thread.Sleep(TimeSpan)"/>, and the attempt count follows from the two
    /// timings rather than from how long the machine happened to take between them.
    /// </summary>
    internal static StreamWriter OpenAppendWriter(
        string logFilePath, TimeSpan retryBudget, TimeSpan retryDelay, Action<TimeSpan> sleep)
    {
        int attempts = retryDelay <= TimeSpan.Zero
            ? 1
            : 1 + (int)Math.Max(0, Math.Floor(retryBudget.TotalMilliseconds / retryDelay.TotalMilliseconds));

        int lastError = 0;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            SafeFileHandle handle = CreateFileW(
                logFilePath,
                FileAppendData | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenAlways,
                FileAttributeNormal,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                FileStream stream = new(handle, FileAccess.Write);
                return new StreamWriter(stream, NoPreambleUtf8) { AutoFlush = true };
            }

            lastError = Marshal.GetLastPInvokeError();
            handle.Dispose();

            if (lastError is not (ErrorSharingViolation or ErrorLockViolation))
            {
                // Not a sharing problem at all — a missing directory, a denied ACL. Retrying a
                // failure that has nothing to do with another process's handle would only
                // delay the fallback by the whole budget and teach the reader nothing.
                throw new IOException($"CreateFile({logFilePath}) failed with Win32 error {lastError}.");
            }

            if (attempt < attempts)
            {
                sleep(retryDelay);
            }
        }

        throw new IOException(
            $"CreateFile({logFilePath}) failed with Win32 error {lastError} ({ErrorName(lastError)}) after retrying for "
            + $"{retryBudget.TotalSeconds:0.#}s across {attempts} attempts. "
            + WindowsFileLockHolders.Describe(logFilePath)
            + LegacyLauncherRemedy(logFilePath));
    }

    /// <summary>
    /// The two error codes the retry waits on, spelled out. Both mean "somebody else has this
    /// file", but they are not the same somebody: 32 is the share-mode conflict this type's
    /// doc is about, and 33 is a byte-range lock, so the message says which one it met rather
    /// than calling either a sharing violation.
    /// </summary>
    private static string ErrorName(int error) => error switch
    {
        ErrorSharingViolation => "sharing violation",
        ErrorLockViolation => "lock violation",
        _ => "unexpected",
    };

    /// <summary>
    /// The one remedy this can name for a sharing violation that never cleared, and it is only
    /// ever the right one when this process's own inherited stdout or stderr is itself a handle
    /// onto the log. Since §16 PLACEHOLDER-d4e64dfa that handle is the launcher's own append handle, which
    /// refuses nobody — so a violation alongside it means a legacy autostart registration is
    /// still launching h9kd through cmd.exe's <c>&gt;&gt;</c> redirect, whose share mode admits
    /// readers and refuses a second writer, and which nothing but
    /// <c>h9k daemon autostart enable</c> rewrites.
    /// <para>
    /// This used to short-circuit the retry outright rather than annotate its give-up: back when
    /// the launcher ALWAYS held the log that way, waiting was provably pointless and three
    /// seconds of dead air at every daemon start was worth avoiding. It would be wrong now — the
    /// inherited handle is no longer a reason a retry cannot succeed, so blaming it would send a
    /// reader after the wrong holder and skip the wait that would actually have worked. The price
    /// is that a node still on the legacy launch script pays the full retry budget once per
    /// start; three seconds on an un-migrated node is the right side of that trade against ever
    /// naming the wrong holder on a migrated one.
    /// </para>
    /// </summary>
    private static string LegacyLauncherRemedy(string logFilePath) =>
        InheritedStandardHandleAlreadyTargets(logFilePath)
            ? " This process's own inherited stdout or stderr also targets that same file, and a handle this "
                + "process inherited lives exactly as long as this process does. An h9kd launched by h9k itself "
                + "inherits an append handle that refuses nobody, so this points at an autostart registration "
                + "written before the launcher opened that handle, whose launch script still redirects through "
                + "cmd.exe's `>>`. Re-run h9k daemon autostart enable to rewrite it."
            : string.Empty;

    /// <summary>
    /// True when this process's own stdout or stderr is already a handle onto
    /// <paramref name="logFilePath"/>. Anything this cannot resolve (a pipe, a console, a path
    /// the kernel will not name) answers false, so an unrecognized case says nothing rather than
    /// guessing.
    /// </summary>
    private static bool InheritedStandardHandleAlreadyTargets(string logFilePath) =>
        StandardHandleTargets(StandardOutputHandle, logFilePath)
        || StandardHandleTargets(StandardErrorHandle, logFilePath);

    private static bool StandardHandleTargets(int standardHandle, string logFilePath)
    {
        IntPtr handle = GetStdHandle(standardHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || GetFileType(handle) != FileTypeDisk)
        {
            return false;
        }

        StringBuilder resolved = new(FinalPathCharacters);
        uint length = GetFinalPathNameByHandleW(handle, resolved, FinalPathCharacters, VolumeNameDos);
        return length != 0 && length < FinalPathCharacters && DescribesSameFile(resolved.ToString(), logFilePath);
    }

    /// <summary>
    /// Whether a path the kernel reported for an open handle and a path h9kd was configured
    /// with name the same file. <c>GetFinalPathNameByHandle</c> answers in extended-length
    /// form (<c>\\?\C:\…</c>, or <c>\\?\UNC\server\share\…</c> for a network path), which is
    /// never the shape <see cref="DaemonRuntime.LogFile"/> holds, so the prefix comes off
    /// before the comparison. Internal for direct coverage: the interop that produces the
    /// left-hand side cannot be exercised from a test process whose own stdout is a pipe.
    /// </summary>
    internal static bool DescribesSameFile(string resolvedHandlePath, string logFilePath) =>
        string.Equals(
            StripExtendedLengthPrefix(resolvedHandlePath),
            StripExtendedLengthPrefix(Path.GetFullPath(logFilePath)),
            StringComparison.OrdinalIgnoreCase);

    private static string StripExtendedLengthPrefix(string path) => path switch
    {
        _ when path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) => @"\\" + path[8..],
        _ when path.StartsWith(@"\\?\", StringComparison.Ordinal) => path[4..],
        _ => path,
    };

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr file);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint pathLength, uint flags);
}
