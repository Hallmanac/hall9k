namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// The cross-platform seam for agent process control (Decisions Log #3): macOS implemented
/// first; Windows is the first dogfooded task post-flip. PID + start time together are a
/// process identity — a bare PID is a lie waiting to happen (PID reuse, log #2).
/// </summary>
public interface IProcessManager
{
    bool IsAlive(int processId, DateTimeOffset startedAt);

    void Terminate(int processId, DateTimeOffset startedAt);
}
