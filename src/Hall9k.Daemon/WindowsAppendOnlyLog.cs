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
/// Unix, both real <c>O_APPEND</c>). The handle h9kd otherwise inherits on Windows — the one
/// cmd.exe opened for its own <c>&gt;&gt;</c> redirect — does not have that property: its
/// write position is a value cached at open time, so a rotation that truncates the file out
/// from under it leaves the next write landing at the old (now past-end-of-file) offset,
/// which Windows answers by zero-filling the gap. The log would then read back at its
/// pre-rotation size, padded with NULs, and the budget <see cref="DaemonLogRotation"/> exists
/// to enforce would never actually hold. Opening a fresh handle with only
/// <c>FILE_APPEND_DATA</c> (never combined with <c>FILE_WRITE_DATA</c>/<c>GENERIC_WRITE</c>)
/// gets the same self-healing-across-truncation property real <c>O_APPEND</c> gives the Unix
/// side, so when this open succeeds, this process's own writes survive a rotation regardless
/// of who performs it. Read the two paragraphs below for where that stands on Windows today:
/// it does not succeed, and the shipped consequence is not the NUL padding described here.
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
/// <strong>What a sharing violation on this open actually means.</strong> Windows decides a
/// second open by checking the requested access against every existing handle's share mode,
/// so this open is refused whenever somebody already holds the log with a share mode that
/// excludes writers. The one such holder h9kd always has is its own launcher: both shipped
/// Windows launch paths run h9kd under <c>cmd.exe /c "h9kd &lt; NUL &gt;&gt; h9kd.log 2&gt;&amp;1"</c>
/// (<c>DaemonLifecycle.SpawnDetachedWindows</c> and <c>WindowsDaemonAutostart</c>'s inner
/// command), and cmd.exe opens an append redirect's target with <c>FILE_SHARE_READ</c> only —
/// readers welcome, a second writer refused — then holds it for the whole run. That handle is
/// this process's own inherited stdout, so no amount of retrying can outlive it, which is why
/// the open checks the one part of that it can actually observe — whether its own inherited
/// stdout or stderr already targets the log — and reports that rather than burning the retry
/// budget at every single daemon start. Measured on Windows 11 Pro 26200, 2026-09-16, against the
/// shape of both launch paths. Fixing it for real means taking cmd.exe's <c>&gt;&gt;</c> off
/// the daemon launch, so the launcher hands h9kd an inheritable <c>FILE_APPEND_DATA</c> handle
/// it opened with a permissive share mode instead: that is its own piece of work, recorded in
/// PLAN.md §16 #217, not this type's job.
/// </para>
/// <para>
/// <strong>What losing this handle actually costs, then.</strong> Not the NUL padding the
/// first paragraph warns of: the same share mode that refuses this open refuses
/// <see cref="DaemonLogRotation"/>'s own <c>FileAccess.ReadWrite</c> open just as flatly
/// (measured the same day), so on Windows no truncation lands under the daemon's inherited
/// handle at all, and there is no zero-filled gap for it to leave behind. What is lost is the
/// budget itself: <c>LogRotationService</c>'s five-minute tick catches the refusal and logs
/// "Log rotation failed; will retry next tick" instead of rolling the log, so an oversized log
/// on a running Windows daemon stays oversized until the CLI's own start path next runs and
/// rotates it while nothing holds it — <c>h9k daemon start</c>, or an <c>h9k install</c> or
/// <c>h9k update</c> that restarts the daemon for you. That path is the only Windows one that
/// rotates: a node that comes up solely through the logon autostart task goes from
/// <c>wscript.exe</c> straight to cmd.exe and never reaches it, so there the budget is not
/// deferred but never enforced at all. The lines themselves are never at risk either way: the
/// inherited handle this falls back to is cmd.exe's redirect onto the very same
/// <c>h9kd.log</c>, so every line still lands there and every reader following it still sees
/// them.
/// </para>
/// <para>
/// A sharing violation from anybody <em>else</em> — a backup agent, an on-access virus
/// scanner, an editor left open on the log — is transient, so the open retries for a few
/// bounded seconds before it gives up. Both sharing-violation give-up paths, the short circuit
/// above and the exhausted budget, name the holder through <see cref="WindowsFileLockHolders"/>
/// rather than reporting a bare Win32 error 32: the two reports on mailbox issue #1 (the Windows
/// project window at the v0.4.0 restart, 2026-09-08 00:28 EDT, and the Windows node window after
/// the v0.5.1 install, 2026-09-09 09:40 EDT) each had to reason backwards from the error number
/// alone, and each landed on the wrong holder.
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

            if (attempt == 1 && InheritedStandardHandleAlreadyTargets(logFilePath))
            {
                throw new IOException(
                    $"CreateFile({logFilePath}) failed with Win32 error {lastError} ({ErrorName(lastError)}), and no "
                    + "retry can clear it: this process's own inherited stdout or stderr already targets that same "
                    + "file, and a handle this process inherited lives exactly as long as this process does. Who "
                    + "opened it is not something this check observes; on both shipped Windows launch paths it is "
                    + "the launcher's cmd.exe `>>` redirect, whose share mode admits readers but refuses a second "
                    + "writer. The holder named next is the measured one. "
                    + WindowsFileLockHolders.Describe(logFilePath));
            }

            if (attempt < attempts)
            {
                sleep(retryDelay);
            }
        }

        throw new IOException(
            $"CreateFile({logFilePath}) failed with Win32 error {lastError} ({ErrorName(lastError)}) after retrying for "
            + $"{retryBudget.TotalSeconds:0.#}s across {attempts} attempts. "
            + WindowsFileLockHolders.Describe(logFilePath));
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
    /// True when this process's own stdout or stderr is already a handle onto
    /// <paramref name="logFilePath"/>. That, and not who opened it, is what makes waiting
    /// pointless: a handle this process inherited is released when this process exits and
    /// not before, so no retry budget can outlast it whatever the launcher was. (On the two
    /// shipped launch paths it is cmd.exe's <c>&gt;&gt;</c> redirect, which is why the message
    /// offers that as the lead while leaving the measured holder to
    /// <see cref="WindowsFileLockHolders"/>.) Anything this cannot resolve (a pipe, a console,
    /// a path the kernel will not name) answers false, so an unrecognized case falls through
    /// to the retry rather than short-circuiting on a guess.
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
