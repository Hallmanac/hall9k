using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Read-side access to ~/.hall9k/h9kd.log while the daemon may be appending to it, plus
/// the rotation that keeps it from growing without end. Every read is bounded: the log
/// is appended to across every start (the CLI redirects with &gt;&gt;, launchd opens
/// StandardOutPath in append mode) and a single failed startup writes kilobytes of
/// Npgsql and Wolverine stack traces, so "print the last eight lines" must not cost a
/// read of the whole file.
/// </summary>
public static class DaemonLog
{
    /// <summary>
    /// How far back a tail reads. Comfortably more than the handful of lines any caller
    /// asks for, even when the last entries are full stack traces, and a constant cost
    /// however large the log has grown.
    /// </summary>
    private const int TailByteBudget = 64 * 1024;

    public static long CurrentLength() => CurrentLength(DaemonRuntime.LogFile);

    public static IReadOnlyList<string> Tail(int lineCount) => Tail(DaemonRuntime.LogFile, lineCount);

    /// <summary>
    /// The daemon's startup catch-up line, from the marker onward — or null when it has
    /// not been written past the given offset yet.
    /// </summary>
    public static string? FindCatchUpReport(long fromOffset) =>
        FindCatchUpReport(DaemonRuntime.LogFile, fromOffset);

    /// <summary>The first line past the offset containing the token — e.g. the "fail:" line that killed a startup.</summary>
    public static string? FindLine(long fromOffset, string token) =>
        FindLine(DaemonRuntime.LogFile, fromOffset, token);

    /// <summary>
    /// Roll an oversized log aside from the start path, keeping one previous generation.
    /// The daemon enforces the same budget on a timer while it runs (a daemon started
    /// once and left up for weeks would otherwise never reach a start path again), so
    /// this is the same copy-then-truncate the running process performs, not a rename.
    /// Returns true when the log was rolled.
    /// </summary>
    public static bool RotateIfOversized() => DaemonLogRotation.RotateIfOversized(DaemonRuntime.LogFile);

    /// <summary>Where the previous generation lands when the log is rolled.</summary>
    public static string PreviousLogFile => DaemonLogRotation.PreviousLogFile(DaemonRuntime.LogFile);

    internal static long CurrentLength(string logFilePath)
    {
        FileInfo log = new(logFilePath);
        return log.Exists ? log.Length : 0;
    }

    internal static IReadOnlyList<string> Tail(string logFilePath, int lineCount)
    {
        if (!File.Exists(logFilePath))
        {
            return [];
        }

        using FileStream stream = OpenShared(logFilePath);
        long from = Math.Max(0, stream.Length - TailByteBudget);
        stream.Seek(from, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8);

        IEnumerable<string> read = reader.ReadToEnd().Split('\n');
        if (from > 0)
        {
            // A read that starts mid-file starts mid-line: that first fragment is not a
            // line of the log and would print as a truncated one.
            read = read.Skip(1);
        }

        List<string> lines = [.. read.Where(line => line.Length > 0)];
        return lines.Count <= lineCount ? lines : lines[^lineCount..];
    }

    internal static string? FindCatchUpReport(string logFilePath, long fromOffset)
    {
        if (!File.Exists(logFilePath))
        {
            return null;
        }

        foreach (string line in ReadFrom(logFilePath, fromOffset).Split('\n'))
        {
            int marker = line.IndexOf(DaemonRuntime.CatchUpMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                return line[marker..].TrimEnd();
            }
        }

        return null;
    }

    internal static string? FindLine(string logFilePath, long fromOffset, string token)
    {
        if (!File.Exists(logFilePath))
        {
            return null;
        }

        return ReadFrom(logFilePath, fromOffset)
            .Split('\n')
            .FirstOrDefault(line => line.Contains(token, StringComparison.Ordinal))?
            .TrimEnd();
    }

    /// <summary>
    /// Read forward from an offset the caller took itself — bounded by what has been
    /// written since, which for the start path is one startup's worth of output.
    /// </summary>
    private static string ReadFrom(string logFilePath, long offset)
    {
        using FileStream stream = OpenShared(logFilePath);
        if (offset >= stream.Length)
        {
            return string.Empty;
        }

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FileStream OpenShared(string logFilePath) => new(
        logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
