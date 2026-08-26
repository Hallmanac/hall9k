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

    /// <summary>
    /// Set to <c>"1"</c> only by the two Windows launch paths that redirect h9kd's
    /// stdout/stderr through cmd.exe's own <c>&gt;&gt;</c> append before h9kd ever starts
    /// (<c>DaemonLifecycle.SpawnDetachedWindows</c> and <c>WindowsDaemonAutostart</c>'s
    /// launch script) — never by a human's shell. Both paths set this through
    /// <c>ProcessStartInfo.Environment</c>, which is seeded from the current process's own
    /// environment, so this name IS forwarded like every other if a parent shell happens to
    /// have it set; what actually keeps this safe is that no supported path ever sets it,
    /// not that inheritance is somehow blocked. An operator exporting this variable by hand
    /// before running h9kd from a terminal would see their own console output silently
    /// redirected into the installed daemon's log — the exact failure this gate exists to
    /// prevent — which is why the name stays internal and undocumented rather than a
    /// supported override. Program.cs reads
    /// it to decide whether swapping in <c>WindowsAppendOnlyLog</c>'s replacement
    /// handles is even applicable: without this gate, every other way of running h9kd on
    /// Windows (a bare terminal invocation, the <c>dotnet run --project Hall9k.AppHost</c>
    /// dev loop) had its console output silently redirected into the installed daemon's
    /// log file instead, because <c>WindowsAppendOnlyLog</c> was applied unconditionally on
    /// the OS check alone rather than on whether stdout was actually the cmd.exe redirect it
    /// exists to survive a rotation of.
    /// </summary>
    public const string AppendOnlyLogEnvironmentVariable = "HALL9K_DAEMON_APPEND_ONLY_LOG";
}
