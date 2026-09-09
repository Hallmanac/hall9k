using Hall9k.Daemon.ProcessManagement;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// A controllable OS seam: tests declare which pids are alive instead of spawning real
/// processes. Start times are accepted as-is — identity verification belongs to the real
/// implementation; these tests exercise who ASKS the OS, not how it answers.
/// </summary>
public sealed class FakeProcessManager : IProcessManager
{
    private readonly HashSet<int> _alive = [];
    private readonly Dictionary<int, List<int>> _descendants = [];
    private int _nextProcessId = 90_000;

    /// <summary>Every (processId, startedAt) pair IsAlive was asked about, in call order.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> LivenessQueries { get; } = [];

    /// <summary>Every process Terminate was called on, in call order — what the seam was ASKED to kill.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> Terminations { get; } = [];

    /// <summary>
    /// Every <see cref="TerminateTree"/> call, in call order, with exactly the pids it reported
    /// back as lingering (root included when alive) — what a test asserts the daemon actually
    /// found and killed before it logged or moved on.
    /// </summary>
    public List<(int ProcessId, DateTimeOffset StartedAt, IReadOnlyList<int> Lingering)> TreeTerminations { get; } = [];

    /// <summary>Every spawn request this seam was asked to satisfy, in call order.</summary>
    public List<ProcessSpawnRequest> Spawns { get; } = [];

    /// <summary>
    /// A fake pid, marked alive: nothing here actually runs <paramref name="request"/>'s
    /// command, so a caller that depends on real output landing in the redirected files
    /// needs a real <see cref="IProcessManager"/>, not this one — this exists for callers
    /// that only care about the identity handed back.
    /// </summary>
    public SpawnedProcess Spawn(ProcessSpawnRequest request)
    {
        Spawns.Add(request);
        int processId = _nextProcessId++;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        MarkAlive(processId);
        return new SpawnedProcess(processId, startedAt);
    }

    public void MarkAlive(int processId) => _alive.Add(processId);

    public void MarkDead(int processId) => _alive.Remove(processId);

    /// <summary>
    /// Declares <paramref name="childProcessId"/> a still-running descendant of
    /// <paramref name="parentProcessId"/> — the shape a session's own lingering background
    /// `dotnet test` takes (task: the daemon terminates a completed session's process tree). Marks
    /// the child alive too, since a descendant a test declares this way is meant to still be
    /// running until <see cref="TerminateTree"/> reaches it.
    /// </summary>
    public void MarkDescendant(int parentProcessId, int childProcessId)
    {
        if (!_descendants.TryGetValue(parentProcessId, out List<int>? children))
        {
            children = [];
            _descendants[parentProcessId] = children;
        }

        children.Add(childProcessId);
        MarkAlive(childProcessId);
    }

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        LivenessQueries.Add((processId, startedAt));
        return _alive.Contains(processId);
    }

    public void Terminate(int processId, DateTimeOffset startedAt)
    {
        Terminations.Add((processId, startedAt));
        _alive.Remove(processId);
    }

    public IReadOnlyList<int> TerminateTree(int processId, DateTimeOffset startedAt)
    {
        if (!_alive.Contains(processId))
        {
            return [];
        }

        List<int> lingering = [processId, .. CollectDescendants(processId)];
        foreach (int pid in lingering)
        {
            _alive.Remove(pid);
        }

        Terminations.Add((processId, startedAt));
        TreeTerminations.Add((processId, startedAt, lingering));
        return lingering;
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
