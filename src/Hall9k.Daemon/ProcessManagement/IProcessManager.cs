namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// What Spawn hands a caller back: enough to track the child later, identically to how
/// every other spawn on this seam is tracked (pid + start time together are a process
/// identity per Decisions Log #2 — a bare pid is a lie waiting to happen).
/// </summary>
public sealed record SpawnedProcess(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// A detached child process request: the inner command to run (already carrying its own
/// arguments and quoting — the platform implementation supplies the shell, not the
/// tokenizing), redirected to files rather than to a pipe this process would have to keep
/// reading from. The child owning its stdout/stderr file handle directly, independent of
/// whatever spawned it, is what lets a daemon restart never interrupt a session already in
/// flight (log #2) — the same property <see cref="IProcessManager.Spawn"/>'s two
/// implementations both have to preserve, just through a different native shell.
/// </summary>
public sealed record ProcessSpawnRequest(
    string Command,
    string WorkingDirectory,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    string? StandardInputFile,
    string StandardOutputFile,
    string StandardErrorFile);

/// <summary>
/// The cross-platform seam for agent process control (Decisions Log #3): macOS implemented
/// first, Windows second (<see cref="UnixProcessManager"/>, <see cref="WindowsProcessManager"/>,
/// Decisions Log #84). PID + start time together are a process identity per Decisions Log #2 —
/// a bare pid is a lie waiting to happen (PID reuse, log #2).
/// </summary>
public interface IProcessManager
{
    /// <summary>
    /// Starts a detached child, redirected to files, and returns immediately with its
    /// identity — never awaited on to finish, since the callers on this seam (agent
    /// sessions) are meant to outlive the daemon call that spawned them.
    /// </summary>
    SpawnedProcess Spawn(ProcessSpawnRequest request);

    bool IsAlive(int processId, DateTimeOffset startedAt);

    void Terminate(int processId, DateTimeOffset startedAt);
}
