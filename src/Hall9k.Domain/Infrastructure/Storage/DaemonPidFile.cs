using System.Text.Json;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The identity the running daemon records for the CLI: pid plus process start time,
/// never a bare pid (Decisions Log #2 — a bare pid is a lie waiting to happen).
/// </summary>
public sealed record DaemonProcessDescriptor(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// Reads and writes the daemon's pid file. Paths are parameters so tests work against
/// temp files; callers pass <see cref="DaemonRuntime.PidFile"/> in production.
/// </summary>
public static class DaemonPidFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static void Write(string path, DaemonProcessDescriptor descriptor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(descriptor, SerializerOptions));
    }

    /// <summary>
    /// Same shape as <see cref="Write"/>, for a caller that already has a
    /// <see cref="CancellationToken"/> in hand and would rather not block on the write —
    /// the Windows stop-request file (<see cref="DaemonRuntime.StopRequestFile"/>) is
    /// written this way so the identity it names carries the same pid-plus-start-time
    /// discipline as the pid file, never a bare pid.
    /// </summary>
    public static Task WriteAsync(string path, DaemonProcessDescriptor descriptor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(descriptor, SerializerOptions), cancellationToken);
    }

    /// <summary>
    /// Null when the file is missing or unreadable — a stale, corrupt or unreadable pid file
    /// never throws. Unreadable covers permissions too: a pid file another account owns
    /// tells us nothing about the daemon, and "not running" is the honest reading of it.
    /// </summary>
    public static DaemonProcessDescriptor? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DaemonProcessDescriptor>(File.ReadAllText(path), SerializerOptions)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A pid file that outlives the process is handled by the reader's aliveness
            // check; failing shutdown over it would be worse than leaving it.
        }
    }
}
