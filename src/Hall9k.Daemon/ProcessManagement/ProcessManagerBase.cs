using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// IsAlive and Terminate have no platform-specific content: <see cref="Process.GetProcessById"/>,
/// <see cref="Process.StartTime"/>, and <c>Kill(entireProcessTree: true)</c> already behave
/// identically on every OS .NET targets here, and the pid-reuse check that tells "same
/// process" from "the pid was recycled" (Decisions Log #2) is a comparison, not a syscall —
/// nothing about it differs by platform. Spawn is one of two places a real difference exists
/// (the native shell that gives the child its own file handle); the other is
/// <see cref="CollectDescendants"/> (task: the daemon terminates a completed session's process
/// tree before it starts any gate or another session in the same worktree), since no
/// cross-platform BCL API enumerates "children of this pid" — every other member here is
/// implemented once, in this base class, in terms of those two.
/// </summary>
public abstract class ProcessManagerBase : IProcessManager
{
    // Start times can drift slightly between recording and reading; a match within this
    // window means "same process", anything further means the PID was reused.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    // How long a process found alive gets to exit on its own before TerminateTree treats it as
    // lingering rather than mid-teardown: the daemon's own poll can land inside the few hundred
    // milliseconds an ordinary session spends exiting after its terminal result line lands on
    // disk (SessionResultWaiter/RunSupervisor return the instant that line is parsed, never
    // waiting for the process itself to exit), so "found alive at this instant" alone cannot
    // tell a well-behaved session still tearing down from the genuinely lingering background
    // gate this method exists to catch — one persists for minutes, not fractions of a second, so
    // this window costs nothing against a real positive.
    private static readonly TimeSpan LingeringGraceWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LingeringGracePollInterval = TimeSpan.FromMilliseconds(25);

    public abstract SpawnedProcess Spawn(ProcessSpawnRequest request);

    /// <summary>
    /// Test-only synchronization seam: fired the instant <see cref="TerminateTree"/> finishes
    /// snapshotting descendants, before its grace-window wait begins. No production caller
    /// subscribes, so this costs nothing outside tests — it exists so
    /// <see cref="Hall9k.Tests.Daemon.ProcessManagerParityTests"/> can kill a root process only
    /// once TerminateTree has genuinely captured its descendants, instead of racing a fixed
    /// delay against thread-pool scheduling to approximate the same ordering.
    /// </summary>
    internal event Action? DescendantsSnapshotted;

    /// <summary>
    /// Reads a just-started process's start time, tolerating the same race
    /// <see cref="TryGet"/> already tolerates on the read side: a command that exits before
    /// this runs (an <c>exec</c>'d one-liner, or cmd.exe racing a trivial child) leaves the OS
    /// with nothing left to report. <see cref="DateTimeOffset.MinValue"/> is recorded instead
    /// of a plausible-looking guess — AGENTS.md's "never guess at unobserved facts" applies
    /// directly here, since this value becomes the process identity <see cref="TryGet"/> and
    /// every later liveness check key off. Stamping "now" would risk a false match if the OS
    /// recycled the pid within <see cref="StartTimeTolerance"/> before the next read; the
    /// sentinel instead guarantees <see cref="TryGet"/> never matches a real process's start
    /// time, so a process that was already gone at spawn time stays reported as gone.
    /// </summary>
    protected static DateTimeOffset ReadStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        return process is not null;
    }

    public void Terminate(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        process?.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// Descendants are enumerated (best-effort, via <see cref="CollectDescendants"/>) BEFORE the
    /// kill: <c>Process.Kill(entireProcessTree: true)</c> already walks and kills them internally,
    /// but it does not hand back who it found, and a lingering child logged by name is the whole
    /// point of this method over the plain <see cref="Terminate"/> above (task: the daemon
    /// terminates a completed session's process tree and logs each lingering process it ended).
    /// Enumeration failing (the platform helper it shells out to is missing, or errors) never
    /// blocks the kill itself — <see cref="CollectDescendants"/> swallows its own failures and
    /// returns empty, so the tree still comes down, just unnamed.
    /// <para>
    /// A process found alive is given <see cref="LingeringGraceWindow"/> to exit on its own
    /// before this reports and kills it (independent pre-PR review, cycle 1, adversarial lens):
    /// without the grace window, a session caught mid-teardown — alive at the instant of the
    /// call, gone a few hundred milliseconds later — was indistinguishable from the pathology
    /// this method exists to catch, so every ordinary completion the daemon's poll happened to
    /// land inside risked logging a false "left process(es) still running" warning and killing a
    /// process that was already exiting on its own.
    /// </para>
    /// <para>
    /// Descendants are enumerated once, up front, before the grace wait even starts (independent
    /// pre-PR review, cycle 2, adversarial lens): a still-alive child gets reparented away from
    /// its dying parent as part of the parent's own exit, essentially atomically with
    /// <c>HasExited</c> becoming observable, so a <see cref="CollectDescendants"/> query issued
    /// only after the root is found to have exited can no longer find that child at all even
    /// though it is still running — exactly the lingering background job (a `dotnet test` a fix
    /// session forgot to foreground) this whole method exists to catch. Enumerating first and
    /// re-checking each of those pids' own liveness afterward means the root exiting inside the
    /// grace window can never make a genuinely lingering descendant invisible.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> TerminateTree(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        if (process is null)
        {
            return [];
        }

        // Start times are snapshotted immediately alongside the pids, not just the bare pids
        // on their own: the grace wait below can run for up to LingeringGraceWindow before
        // these are used again, and a bare pid re-checked that much later is exactly the
        // pid-reuse race TryGet's own start-time comparison exists to close everywhere else in
        // this file (Decisions Log #2) — the "root still alive" branch already gets this for
        // free by killing through the root's own live Process handle instead of re-resolving a
        // pid, so the reparented-descendant branch below needs the same identity discipline
        // rather than a bare-pid lookup (independent pre-PR review, cycle 3, adversarial lens).
        List<(int Id, DateTimeOffset StartedAt)> descendantSnapshotsBeforeExit =
            [.. CollectDescendants(processId).Select(TrySnapshotStartTime).OfType<(int Id, DateTimeOffset StartedAt)>()];

        // A throwing subscriber must never reach here: this is a test-only seam (see the event's
        // own doc comment), and letting an exception from it propagate would abort process-tree
        // cleanup and bubble into the run pipeline for something no production caller even wires up.
        try
        {
            DescendantsSnapshotted?.Invoke();
        }
        catch (Exception)
        {
        }

        DateTimeOffset graceDeadline = DateTimeOffset.UtcNow + LingeringGraceWindow;
        while (!process.HasExited && DateTimeOffset.UtcNow < graceDeadline)
        {
            Thread.Sleep(LingeringGracePollInterval);
        }

        if (!process.HasExited)
        {
            List<int> lingering = [processId, .. CollectDescendants(processId)];
            process.Kill(entireProcessTree: true);

            // entireProcessTree only reaches what is still parented under the root at the
            // instant of the kill: a descendant reparented away during the grace wait above is
            // just as unreachable here as it is in the root-exited branch below, so the same
            // pre-grace snapshot is re-checked by pid-and-start-time identity rather than
            // trusting the live tree alone (independent pre-PR review, cycle 1, adversarial
            // lens).
            foreach ((int descendantId, DateTimeOffset descendantStartedAt) in descendantSnapshotsBeforeExit)
            {
                if (lingering.Contains(descendantId))
                {
                    continue;
                }

                using Process? descendant = TryGet(descendantId, descendantStartedAt);
                if (descendant is null)
                {
                    continue;
                }

                lingering.Add(descendantId);
                descendant.Kill(entireProcessTree: true);
            }

            return lingering;
        }

        // The root exited on its own within the grace window: entireProcessTree has nothing
        // left to walk from, so a descendant that outlived it (now reparented, and no longer
        // reachable through CollectDescendants at all) is killed individually — through TryGet,
        // by the pid-and-start-time identity this method snapshotted before the reparenting
        // could have happened, so a pid the OS recycled for an unrelated process in the
        // meantime is recognized as gone rather than killed by mistake.
        //
        // A descendant still alive at the exact instant the root happens to exit is not yet
        // distinguishable from one genuinely lingering (independent pre-PR review, cycle 1,
        // adversarial lens): the root exiting early only proves the ROOT finished tearing down,
        // not that everything it spawned already has, and the doc comment above's own reasoning
        // for giving the root a grace window before judging it applies verbatim here. Rather
        // than a second, fresh window — which would let a slow root plus a slow descendant cost
        // up to double LingeringGraceWindow in total — this polls out whatever time is LEFT on
        // the SAME graceDeadline already budgeted above, so the combined wait for root-plus-
        // descendants never exceeds the one window this method has always promised.
        while (DateTimeOffset.UtcNow < graceDeadline
            && descendantSnapshotsBeforeExit.Any(descendant => IsAlive(descendant.Id, descendant.StartedAt)))
        {
            Thread.Sleep(LingeringGracePollInterval);
        }

        List<int> stillAliveDescendants = [];
        foreach ((int descendantId, DateTimeOffset descendantStartedAt) in descendantSnapshotsBeforeExit)
        {
            using Process? descendant = TryGet(descendantId, descendantStartedAt);
            if (descendant is null)
            {
                continue;
            }

            stillAliveDescendants.Add(descendantId);
            descendant.Kill(entireProcessTree: true);
        }

        return stillAliveDescendants;
    }

    private static (int Id, DateTimeOffset StartedAt)? TrySnapshotStartTime(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return (processId, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone by the time this snapshot ran — nothing to track or kill later.
            return null;
        }
    }

    /// <summary>
    /// Best-effort enumeration of every currently-alive child of <paramref name="processId"/>,
    /// recursively. While the root is still alive, this is purely for naming a lingering process
    /// in a log line — <see cref="TerminateTree"/>'s own <c>Kill(entireProcessTree: true)</c> does
    /// the actual kill-walk itself, natively, regardless of what this returns. If the root has
    /// already exited by the time <see cref="TerminateTree"/> checks, though, this enumeration
    /// (captured before that exit, since a reparented child is no longer reachable through it
    /// afterward) is the only record of who else needs killing, so <see cref="TerminateTree"/>
    /// kills each of those pids itself in that case. No cross-platform BCL API exists for
    /// "children of this pid", so each concrete manager shells out to whatever the native OS
    /// already ships for it — <see cref="UnixProcessManager"/> uses <c>pgrep -P</c>,
    /// <see cref="WindowsProcessManager"/> queries WMI through PowerShell's CIM cmdlets.
    /// </summary>
    protected abstract IReadOnlyList<int> CollectDescendants(int processId);

    /// <summary>
    /// How long a platform's own child-enumeration helper (<c>pgrep</c>, a PowerShell CIM query)
    /// gets to finish before <see cref="ReadOutputWithBoundedWait"/> gives up on it and reads back
    /// no output — enumeration failing this way is documented as an ordinary, tolerated outcome
    /// (<see cref="CollectDescendants"/>'s own doc); hanging is not.
    /// </summary>
    protected static readonly TimeSpan ChildProcessQueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs an already-started, stdout/stderr-redirected helper process to completion and hands
    /// back its stdout — shared by both platforms' own <c>ChildrenOf</c> (independent pre-PR
    /// review, cycle 1, adversarial lens). Reading stdout alone and then calling a plain
    /// <c>WaitForExit()</c> is the classic synchronous-redirect deadlock the moment the helper
    /// writes enough to stderr to fill its own pipe before exiting: nothing would be draining it
    /// while <c>WaitForExit</c> blocks, and the helper blocks writing right back, so the two wait
    /// on each other forever. Both streams are read concurrently with the bounded wait below —
    /// stderr's own content is never needed, only that something keeps draining it — and
    /// <paramref name="timeout"/> caps the whole call so a wedged helper (a slow WMI query, a hung
    /// shell) can never block the daemon's own monitor loop indefinitely.
    /// </summary>
    protected static string ReadOutputWithBoundedWait(Process process, TimeSpan timeout)
    {
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKillBestEffort(process);
            return string.Empty;
        }

        return stdout.GetAwaiter().GetResult();
    }

    private static void TryKillBestEffort(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already exited between the timeout check above and this call — nothing left to kill.
        }
    }

    /// <summary>
    /// Shared breadth-first walk over whatever single-level "children of this pid" lookup a
    /// concrete manager's own <see cref="CollectDescendants"/> override supplies. Tracks pids
    /// already enqueued so a cycle in that lookup terminates instead of spinning forever
    /// (independent pre-PR review, cycle 1, adversarial lens): <see cref="WindowsProcessManager"/>'s
    /// map is built from <c>Win32_Process.ParentProcessId</c>, which the OS never clears when a
    /// parent exits, so a recycled pid can produce a two-node cycle (A's stale parent pid is
    /// later reused by a process the live tree also reports as A's own child) that this walk
    /// would otherwise revisit without end, growing <c>descendants</c> unboundedly on the thread
    /// that is supposed to be acting on a session's terminal result.
    /// </summary>
    protected static IReadOnlyList<int> CollectDescendantsBreadthFirst(int rootProcessId, Func<int, IEnumerable<int>> childrenOf)
    {
        List<int> descendants = [];
        HashSet<int> enqueued = [rootProcessId];
        Queue<int> frontier = new();
        frontier.Enqueue(rootProcessId);
        while (frontier.Count > 0)
        {
            int parent = frontier.Dequeue();
            foreach (int child in childrenOf(parent))
            {
                if (!enqueued.Add(child))
                {
                    continue;
                }

                descendants.Add(child);
                frontier.Enqueue(child);
            }
        }

        return descendants;
    }

    /// <summary>
    /// Shared line-oriented pid parsing for both platforms' own <c>CollectDescendants</c>
    /// override: <c>pgrep -P</c>'s output and <c>Get-CimInstance</c>'s <c>ProcessId</c> list are
    /// both one integer per line. A line that fails to parse is dropped rather than throwing —
    /// best-effort enumeration, per <see cref="CollectDescendants"/>'s own doc, so stray shell
    /// output never turns a successful (if incomplete) enumeration into a failed one.
    /// </summary>
    protected static IEnumerable<int> ParsePids(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => int.TryParse(line, out int pid) ? pid : (int?)null)
            .OfType<int>();

    private static Process? TryGet(int processId, DateTimeOffset startedAt)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            DateTimeOffset actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if ((actualStart - startedAt).Duration() > StartTimeTolerance)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Win32Exception: StartTime is unreadable because the pid now belongs to
            // another user's (often privileged) process — nothing the daemon spawned,
            // so the recorded process is gone.
            return null;
        }
    }
}
