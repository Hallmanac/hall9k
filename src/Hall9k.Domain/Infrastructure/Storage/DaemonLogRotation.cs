namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Keeps ~/.hall9k/h9kd.log from growing for as long as the node lives. The log is the
/// daemon's redirected stdout/stderr, appended to across every start and never truncated
/// by anything else, so somebody has to enforce a budget.
/// <para>
/// Rotation is a copy-then-truncate-in-place, never a rename, because the process that
/// is writing the log holds its file descriptor open for its whole lifetime: renaming
/// the file would leave that descriptor pointing at the rolled-aside inode and every
/// subsequent line would land in the previous generation, invisibly. Truncating the same
/// inode works with the writer instead of behind it — both the CLI's <c>&gt;&gt;</c>
/// redirect and launchd's StandardOutPath open with O_APPEND, so the next write lands at
/// the new end of the emptied file. Windows has no equivalent for a plain <c>cmd.exe
/// &gt;&gt;</c> handle (its write position is cached at open time rather than re-resolved
/// per write), so h9kd tries to replace its inherited stdout/stderr with true append-only
/// handles of its own before it logs anything — see <c>WindowsAppendOnlyLog</c> in
/// Hall9k.Daemon. Be honest about where that stands: cmd.exe holds an append redirect's
/// target with <c>FILE_SHARE_READ</c> only, for the whole run, so on both shipped Windows
/// launch paths that replacement is refused — and so is the <c>FileAccess.ReadWrite</c> open
/// below, on the identical grounds. That is why a running Windows daemon leaves no
/// NUL-padded log behind: the truncation never lands at all, <c>LogRotationService</c> logs
/// the refusal every five minutes instead, and the budget is enforced only when the CLI's own
/// start path next runs and rotates the log while nothing holds it — <c>h9k daemon start</c>, or
/// an <c>h9k install</c> or <c>h9k update</c> that restarts the daemon. A Windows node that comes
/// up solely through the logon autostart task never reaches that path, so there the budget is
/// never enforced at all rather than merely deferred. The
/// fix — hand the daemon an inheritable append handle instead of a cmd.exe redirect — is
/// recorded in PLAN.md §16 #PLACEHOLDER-3abf032d.
/// </para>
/// <para>
/// The trade a copy-truncate makes is a narrow window: lines written between the copy
/// reading end-of-file and the truncate landing are lost. That window is sub-millisecond
/// and costs at most a line or two of an 8 MB log, which is the same bargain logrotate's
/// copytruncate makes and far cheaper than the alternative of losing every line written
/// after a rename.
/// </para>
/// </summary>
public static class DaemonLogRotation
{
    /// <summary>
    /// Roll the log once it passes this. One previous generation is kept, so the log
    /// costs at most twice this on disk.
    /// </summary>
    public const long ThresholdBytes = 8L * 1024 * 1024;

    private const string PreviousSuffix = ".1";

    /// <summary>Where the previous generation lands when the log is rolled.</summary>
    public static string PreviousLogFile(string logFilePath) => logFilePath + PreviousSuffix;

    /// <summary>
    /// Copy an oversized log aside (replacing the one previous generation) and truncate
    /// it in place, leaving the writer's descriptor valid. Safe to call while the daemon
    /// is running — that is the case it exists for. Returns true when the log was rolled.
    /// </summary>
    public static bool RotateIfOversized(string logFilePath, long thresholdBytes = ThresholdBytes)
    {
        FileInfo log = new(logFilePath);
        if (!log.Exists || log.Length <= thresholdBytes)
        {
            return false;
        }

        using FileStream current = new(
            logFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // Re-check under the handle: another rotator (the CLI's start path racing the
        // daemon's timer) may have truncated it between the FileInfo read and here.
        if (current.Length <= thresholdBytes)
        {
            return false;
        }

        using (FileStream previous = new(
            PreviousLogFile(logFilePath), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            current.CopyTo(previous);
        }

        current.SetLength(0);
        return true;
    }
}
