namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The on-disk contract between the CLI and the daemon (Decisions Log #31): where the
/// installed binaries live, where the daemon logs, and where it records its identity
/// while running. Both sides read these paths; neither talks to the other directly —
/// the filesystem is the seam, the same way Postgres is the seam for domain state.
/// </summary>
public static class DaemonRuntime
{
    /// <summary>Installed release binaries (h9k + h9kd), refreshed by h9k install.</summary>
    public static string BinDirectory => Path.Combine(RunPaths.Root, "bin");

    /// <summary>The staging directory h9k install publishes into before the swap.</summary>
    public static string StagingBinDirectory => Path.Combine(RunPaths.Root, "bin.staging");

    /// <summary>The daemon's log: its redirected stdout/stderr, appended across starts.</summary>
    public static string LogFile => Path.Combine(RunPaths.Root, "h9kd.log");

    /// <summary>
    /// Written by the running daemon (pid + process start time — a process identity per
    /// Decisions Log #2, never a bare pid), deleted on graceful exit. The CLI verifies
    /// the recorded process is actually alive before believing this file.
    /// </summary>
    public static string PidFile => Path.Combine(RunPaths.Root, "h9kd.pid");

    /// <summary>
    /// The single-instance lock the daemon holds open for its lifetime. Separate from
    /// the pid file so the CLI can always read the latter while the lock is held.
    /// </summary>
    public static string LockFile => Path.Combine(RunPaths.Root, "h9kd.lock");

    /// <summary>
    /// The first token of the daemon's startup catch-up log line (what it adopted,
    /// swept, and closed out while down). h9k daemon start tails the log for this
    /// marker so "on demand" shows its latency cost the moment it is paid.
    /// </summary>
    public const string CatchUpMarker = "Catch-up complete";

    /// <summary>
    /// Windows has no SIGTERM a CLI can send to an arbitrary process (Decisions Log #3,
    /// S1-14): this file is the graceful-stop request in its place. <c>h9k daemon stop</c>
    /// writes it; the daemon polls for it (<c>WindowsStopRequestWatcher</c>) and deletes it
    /// on the way down, the same file-based "doorbell plus poll backstop" idiom every other
    /// daemon loop already uses, rather than the fragile Win32 console-signal dance most
    /// remote-graceful-stop tools reach for. Unix keeps using a real SIGTERM — this path
    /// only exists on Windows and never gets written or watched anywhere else.
    /// </summary>
    public static string StopRequestFile => Path.Combine(RunPaths.Root, "h9kd.stop");
}
