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

    /// <summary>Every (processId, startedAt) pair IsAlive was asked about, in call order.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> LivenessQueries { get; } = [];

    /// <summary>Every process Terminate was called on, in call order — what the seam was ASKED to kill.</summary>
    public List<(int ProcessId, DateTimeOffset StartedAt)> Terminations { get; } = [];

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
