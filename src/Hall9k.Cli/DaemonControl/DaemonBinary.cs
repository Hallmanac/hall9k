using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Where h9k finds the h9kd binary to launch: the installed location first
/// (~/.hall9k/bin, refreshed by h9k install), then beside the running h9k (a published
/// pair), then the dev-loop sibling project's build output — so h9k daemon start works
/// from a plain dotnet build too.
/// </summary>
public static class DaemonBinary
{
    private static readonly string BinaryName = OperatingSystem.IsWindows() ? "h9kd.exe" : "h9kd";

    public static string? Locate()
    {
        string installed = Path.Combine(DaemonRuntime.BinDirectory, BinaryName);
        if (File.Exists(installed))
        {
            return installed;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string alongside = Path.Combine(baseDirectory, BinaryName);
        if (File.Exists(alongside))
        {
            return alongside;
        }

        // Dev build: .../src/Hall9k.Cli/bin/<Config>/net10.0/ → the daemon project's
        // matching output. A path-segment swap keeps the configuration in sync.
        string sibling = Path.Combine(
            baseDirectory.Replace(
                $"{Path.DirectorySeparatorChar}Hall9k.Cli{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}Hall9k.Daemon{Path.DirectorySeparatorChar}"),
            BinaryName);
        return File.Exists(sibling) ? sibling : null;
    }

    /// <summary>
    /// Resolves an explicit --binary path to an absolute one against the caller's
    /// working directory. The detach intermediary runs in ~/.hall9k, so a relative
    /// override handed through unresolved would name a different file there — or, far
    /// more often, no file at all. Returns null when the path is malformed rather than
    /// merely absent; the caller checks existence so it can name the resolved path.
    /// </summary>
    public static string? ResolveOverride(string path, string workingDirectory)
    {
        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
