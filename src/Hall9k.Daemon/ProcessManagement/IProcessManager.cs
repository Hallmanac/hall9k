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
/// Decisions Log #85). PID + start time together are a process identity per Decisions Log #2 —
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

    /// <summary>
    /// Terminates the whole process tree rooted at <paramref name="processId"/> — the process
    /// itself and every descendant still alive at the moment this is called — and returns every
    /// pid that was actually found alive and killed (root included), for a caller to log by the
    /// session it belonged to. Empty when the root process is already gone (per the same
    /// pid+start-time identity <see cref="IsAlive"/> and <see cref="Terminate"/> use): a dead
    /// root's former children have already been reparented away from it by the OS, with no
    /// relation to it left to observe, so there is nothing left to name here (task: the daemon
    /// terminates a completed session's process tree before starting a gate or another session in
    /// the same worktree).
    /// </summary>
    IReadOnlyList<int> TerminateTree(int processId, DateTimeOffset startedAt);

    /// <summary>
    /// Best-effort, non-lethal snapshot of every currently-alive descendant of
    /// <paramref name="processId"/>, paired with each one's own start time so a caller can
    /// re-check its identity later without the bare-pid reuse risk Decisions Log #2 exists to
    /// close — nothing is killed here. Empty when the root itself is not alive under the given
    /// identity, or when enumeration finds nothing.
    /// <para>
    /// A caller waiting on a session that must be allowed to keep running past its first result
    /// line (a stream can hold more than one — discovery cc9b7aec) cannot call
    /// <see cref="TerminateTree"/> the instant that line appears without risking killing a
    /// session that was always going to finish on its own; it can only act once the root is
    /// confirmed dead. But <see cref="TerminateTree"/>'s own snapshot-then-kill sequence needs to
    /// run before that confirmation, not after — a descendant reparents away from its dying
    /// parent essentially atomically with the parent's own exit becoming observable, so a
    /// snapshot attempted only after death is confirmed can no longer find it at all. Refreshing
    /// this on every poll while the root is still alive keeps a recent, pre-death view on hand,
    /// so a caller can still individually terminate whatever the root leaves behind even though
    /// the root itself is gone by the time that decision is safe to make.
    /// </para>
    /// </summary>
    IReadOnlyList<(int ProcessId, DateTimeOffset StartedAt)> SnapshotDescendants(int processId, DateTimeOffset startedAt);
}
