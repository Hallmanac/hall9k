using System.Text.Json;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Reads and writes <see cref="DaemonRuntime.StartingMarkerFile"/>: the CLI's own record
/// that it just spawned a launch attempt, for the window before the daemon's pid file
/// exists to prove that attempt succeeded. Paths are parameters so tests work against
/// temp files; callers pass <see cref="DaemonRuntime.StartingMarkerFile"/> in production.
/// </summary>
public static class DaemonStartingMarker
{
    /// <summary>
    /// How long a marker is trusted as evidence of an in-flight boot before it reads the
    /// same as no marker at all. Generous past the field-observed ~15s worst case (the Arx
    /// Windows node, 2026-09-03) so a spawn that genuinely wedged or died before ever
    /// writing a pid file eventually reads as "not running" again, rather than "starting"
    /// forever.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);

    public static void Write(string path, DateTimeOffset spawnedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(spawnedAt));
    }

    /// <summary>
    /// Deletes the starting marker, matching <see cref="DaemonPidFile.Delete"/>'s own
    /// discipline: <see cref="File.Delete(string)"/> is already a no-op against a missing
    /// file, so what this guards against is a missing directory
    /// (<see cref="DirectoryNotFoundException"/>, caught here as <see cref="IOException"/>)
    /// or a locked or unwritable file — either of which a caller never has to check for
    /// first.
    /// </summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A marker that outlives its usefulness is harmless — IndicatesRecentLaunch
            // ages it out on its own. Failing here would be worse than leaving it.
        }
    }

    /// <summary>
    /// True when a marker exists and is fresh enough to still count as an in-flight boot.
    /// A missing, corrupt, or stale marker all read as "no evidence of a boot in
    /// progress" — never an error, the same discipline <see cref="DaemonPidFile.TryRead"/>
    /// applies to the pid file.
    /// </summary>
    public static bool IndicatesRecentLaunch(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            DateTimeOffset spawnedAt = JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(path));
            return (now - spawnedAt).Duration() <= GracePeriod;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }
}
