using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// A process table a test writes by hand: which pids are running, and when each one started.
/// The seam that lets the register refusal and the loss transition be exercised without a real
/// process, and without this suite ever spawning one to kill (AGENTS.md: a dispatched session
/// never generates host load to prove a timing-dependent test).
/// </summary>
public sealed class FakeOrchestratorProcessProbe : IOrchestratorProcessProbe
{
    private readonly Dictionary<int, DateTimeOffset?> _running = [];

    /// <summary>A process with a readable start time — the ordinary case.</summary>
    public FakeOrchestratorProcessProbe Running(int processId, DateTimeOffset startedAt)
    {
        _running[processId] = startedAt;
        return this;
    }

    /// <summary>A process this user can see but whose start time it cannot read (an elevated one).</summary>
    public FakeOrchestratorProcessProbe RunningWithUnreadableStartTime(int processId)
    {
        _running[processId] = null;
        return this;
    }

    /// <summary>The process left, however it left.</summary>
    public FakeOrchestratorProcessProbe Gone(int processId)
    {
        _running.Remove(processId);
        return this;
    }

    public OrchestratorProcessSighting? Probe(int processId) =>
        _running.TryGetValue(processId, out DateTimeOffset? startedAt)
            ? new OrchestratorProcessSighting(startedAt)
            : null;
}
