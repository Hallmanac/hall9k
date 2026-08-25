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
/// side, so this process's own writes survive a rotation regardless of who performs it.
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
    /// </summary>
    public static void TakeOverConsoleOutput(string logFilePath)
    {
        Console.SetOut(OpenAppendWriter(logFilePath));
        Console.SetError(OpenAppendWriter(logFilePath));
    }

    /// <summary>Internal for direct unit coverage against a real truncate race, the same way <see cref="WindowsDaemonAutostart.RecordedVariableNames"/> is tested without going through a live schtasks registration.</summary>
    internal static StreamWriter OpenAppendWriter(string logFilePath)
    {
        SafeFileHandle handle = CreateFileW(
            logFilePath,
            FileAppendData | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenAlways,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"CreateFile({logFilePath}) failed with Win32 error {error}.");
        }

        FileStream stream = new(handle, FileAccess.Write);
        return new StreamWriter(stream, NoPreambleUtf8) { AutoFlush = true };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
