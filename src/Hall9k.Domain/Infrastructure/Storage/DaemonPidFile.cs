using System.Text.Json;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The identity the running daemon records for the CLI: pid plus process start time,
/// never a bare pid (Decisions Log #2 — a bare pid is a lie waiting to happen).
/// </summary>
public sealed record DaemonProcessDescriptor(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// What the daemon inherited of <c>CLAUDE_CONFIG_DIR</c> and nothing else: the value, or null when it
/// inherited none (an empty value counts as none, since Claude Code treats it as unset).
/// </summary>
public sealed record DaemonInheritedEnvironment(string? ClaudeConfigDirectory)
{
    public static DaemonInheritedEnvironment Read(Func<string, string?> read) =>
        new(read(Features.Connection.ClaudeConfigDirectory.Variable) is { Length: > 0 } value ? value : null);
}

/// <summary>
/// The pid file read whole. <see cref="Inherited"/> is null only for a file an older build wrote, which
/// recorded nothing about its environment, so it reads differently from a daemon that inherited none.
/// </summary>
public sealed record DaemonPidRecord(DaemonProcessDescriptor Daemon, DaemonInheritedEnvironment? Inherited);

/// <summary>
/// Reads and writes the daemon's pid file. Paths are parameters so tests work against
/// temp files; callers pass <see cref="DaemonRuntime.PidFile"/> in production.
/// </summary>
public static class DaemonPidFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The pid file's own shape. The descriptor stays apart from it, so the stop-request file, which
    /// serializes a bare descriptor, never carries the inherited environment.
    /// </summary>
    private sealed record PidFileContent(int ProcessId, DateTimeOffset StartedAt, DaemonInheritedEnvironment? InheritedEnvironment);

    public static void Write(string path, DaemonProcessDescriptor descriptor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(descriptor, SerializerOptions));
    }

    /// <summary>The daemon's own write: its identity plus what it inherited of <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static void Write(string path, DaemonProcessDescriptor descriptor, DaemonInheritedEnvironment inherited)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new PidFileContent(descriptor.ProcessId, descriptor.StartedAt, inherited), SerializerOptions));
    }

    /// <summary>
    /// Same shape as <see cref="Write"/>, for a caller that already has a
    /// <see cref="CancellationToken"/> in hand and would rather not block on the write —
    /// the Windows stop-request file (<see cref="DaemonRuntime.StopRequestFile"/>) is
    /// written this way so the identity it names carries the same pid-plus-start-time
    /// discipline as the pid file, never a bare pid.
    /// <see cref="AtomicFileWrite"/> rather than a plain <c>File.WriteAllTextAsync</c>: the
    /// stop-request file has a concurrent reader (<c>WindowsStopRequestWatcher</c>, polling
    /// every 250ms) that this write must never observe half-written, and staging into a temp
    /// file and renaming it into place means the watcher's read is racing an atomic rename
    /// rather than a truncate-then-write with a real content-writing window (cycle-7 pre-PR
    /// review finding).
    /// </summary>
    public static Task WriteAsync(string path, DaemonProcessDescriptor descriptor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return AtomicFileWrite.WriteAllTextAsync(path, JsonSerializer.Serialize(descriptor, SerializerOptions), cancellationToken);
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

    /// <summary>
    /// <see cref="TryRead"/> with the inherited environment, under the same rule: a missing, corrupt or
    /// unreadable file reads as null. Says nothing about whether the daemon is alive; check that against
    /// <see cref="DaemonPidRecord.Daemon"/> before trusting it.
    /// </summary>
    public static DaemonPidRecord? TryReadRecord(string path)
    {
        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<PidFileContent>(File.ReadAllText(path), SerializerOptions) is { } content
                    ? new DaemonPidRecord(new DaemonProcessDescriptor(content.ProcessId, content.StartedAt), content.InheritedEnvironment)
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
