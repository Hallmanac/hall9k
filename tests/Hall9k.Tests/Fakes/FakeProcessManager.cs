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
    private int _nextProcessId = 90_000;

    /// <summary>Every (processId, startedAt) pair IsAlive was asked about, in call order.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> LivenessQueries { get; } = [];

    /// <summary>Every process Terminate was called on, in call order — what the seam was ASKED to kill.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> Terminations { get; } = [];

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
}
