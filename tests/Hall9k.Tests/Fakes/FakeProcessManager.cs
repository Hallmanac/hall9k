using Hall9k.Daemon.ProcessManagement;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// A controllable OS seam: tests declare which pids are alive instead of spawning real
/// processes. Start times are accepted as-is — identity verification belongs to the real
/// implementation; these tests exercise who ASKS the OS, not how it answers.
/// </summary>
public sealed class FakeProcessManager : IProcessManager
{
    // Guards every field below. A scripted spawn's background continuation calls MarkDead
    // from its own task while SessionResultWaiter.WaitAsync polls IsAlive on the daemon
    // thread — both used to touch a plain HashSet<int> unguarded, which .NET's own
    // concurrent-mutation detection can throw on (independent pre-PR review, adversarial
    // lens, cycle 1) — the same shape ListLogger's own lock exists to close.
    private readonly object _gate = new();
    private readonly HashSet<int> _alive = [];
    private readonly Dictionary<int, List<int>> _descendants = [];
    private readonly List<(int ProcessId, DateTimeOffset StartedAt)> _livenessQueries = [];
    private readonly List<(int ProcessId, DateTimeOffset StartedAt)> _terminations = [];
    private readonly List<(int ProcessId, DateTimeOffset StartedAt, IReadOnlyList<int> Lingering)> _treeTerminations = [];
    private readonly List<ProcessSpawnRequest> _spawns = [];
    private int _nextProcessId = 90_000;

    /// <summary>Every (processId, startedAt) pair IsAlive was asked about, in call order.</summary>
    public IReadOnlyList<(int ProcessId, DateTimeOffset StartedAt)> LivenessQueries
    {
        get { lock (_gate) { return [.. _livenessQueries]; } }
    }

    /// <summary>Every process Terminate was called on, in call order — what the seam was ASKED to kill.</summary>
    public IReadOnlyList<(int ProcessId, DateTimeOffset StartedAt)> Terminations
    {
        get { lock (_gate) { return [.. _terminations]; } }
    }

    /// <summary>
    /// Every <see cref="TerminateTree"/> call, in call order, with exactly the pids it reported
    /// back as lingering (root included when alive) — what a test asserts the daemon actually
    /// found and killed before it logged or moved on.
    /// </summary>
    public IReadOnlyList<(int ProcessId, DateTimeOffset StartedAt, IReadOnlyList<int> Lingering)> TreeTerminations
    {
        get { lock (_gate) { return [.. _treeTerminations]; } }
    }

    /// <summary>Every spawn request this seam was asked to satisfy, in call order.</summary>
    public IReadOnlyList<ProcessSpawnRequest> Spawns
    {
        get { lock (_gate) { return [.. _spawns]; } }
    }

    /// <summary>
    /// A fake pid, marked alive: nothing here actually runs <paramref name="request"/>'s
    /// command, so a caller that depends on real output landing in the redirected files
    /// needs a real <see cref="IProcessManager"/>, not this one — this exists for callers
    /// that only care about the identity handed back.
    /// </summary>
    public SpawnedProcess Spawn(ProcessSpawnRequest request)
    {
        int processId;
        lock (_gate)
        {
            _spawns.Add(request);
            processId = _nextProcessId++;
            _alive.Add(processId);
        }

        return new SpawnedProcess(processId, DateTimeOffset.UtcNow);
    }

    public void MarkAlive(int processId)
    {
        lock (_gate) { _alive.Add(processId); }
    }

    public void MarkDead(int processId)
    {
        lock (_gate) { _alive.Remove(processId); }
    }

    /// <summary>
    /// Declares <paramref name="childProcessId"/> a still-running descendant of
    /// <paramref name="parentProcessId"/> — the shape a session's own lingering background
    /// `dotnet test` takes (task: the daemon terminates a completed session's process tree). Marks
    /// the child alive too, since a descendant a test declares this way is meant to still be
    /// running until <see cref="TerminateTree"/> reaches it.
    /// </summary>
    public void MarkDescendant(int parentProcessId, int childProcessId)
    {
        lock (_gate)
        {
            if (!_descendants.TryGetValue(parentProcessId, out List<int>? children))
            {
                children = [];
                _descendants[parentProcessId] = children;
            }

            children.Add(childProcessId);
            _alive.Add(childProcessId);
        }
    }

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _livenessQueries.Add((processId, startedAt));
            return _alive.Contains(processId);
        }
    }

    public void Terminate(int processId, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _terminations.Add((processId, startedAt));
            _alive.Remove(processId);
        }
    }

    public IReadOnlyList<int> TerminateTree(int processId, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            // The real implementation still walks and kills whatever is left running under a
            // root that has already exited on its own — a lingering descendant, reparented away
            // from its dying parent, is exactly the pathology this method exists to catch
            // (ProcessManagerBase.TerminateTree's own "root exited" branch) — so this never bails
            // out early just because the root itself is no longer alive. Every call is recorded
            // here whether or not it finds anything still alive to kill, the same idempotent-
            // no-op-but-still-asked shape Process.Kill on an already-exited handle has.
            IEnumerable<int> candidates = _alive.Contains(processId)
                ? [processId, .. CollectDescendants(processId)]
                : CollectDescendants(processId);
            List<int> lingering = [.. candidates.Where(_alive.Contains)];
            foreach (int pid in lingering)
            {
                _alive.Remove(pid);
            }

            _terminations.Add((processId, startedAt));
            _treeTerminations.Add((processId, startedAt, lingering));
            return lingering;
        }
    }

    private IReadOnlyList<int> CollectDescendants(int rootProcessId)
    {
        List<int> descendants = [];
        HashSet<int> enqueued = [rootProcessId];
        Queue<int> frontier = new();
        frontier.Enqueue(rootProcessId);
        while (frontier.Count > 0)
        {
            int parent = frontier.Dequeue();
            if (!_descendants.TryGetValue(parent, out List<int>? children))
            {
                continue;
            }

            foreach (int child in children)
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
}
