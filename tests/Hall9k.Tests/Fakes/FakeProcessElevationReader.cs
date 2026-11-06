using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Token elevation a test writes by hand per pid, with every read counted so the per-process cache
/// can be pinned. A pid with nothing written reads as <see cref="ProcessElevation.Unknown"/>, the
/// way another account's process reads for real.
/// </summary>
public sealed class FakeProcessElevationReader : IProcessElevationReader
{
    private readonly Dictionary<int, ProcessElevation> _levels = [];
    private readonly Dictionary<int, int> _reads = [];

    public bool IsSupported { get; init; } = true;

    public FakeProcessElevationReader With(int processId, ProcessElevation elevation)
    {
        _levels[processId] = elevation;
        return this;
    }

    /// <summary>This test process's own level, which is what a daemon under test reads for itself.</summary>
    public FakeProcessElevationReader Self(ProcessElevation elevation) => With(Environment.ProcessId, elevation);

    public int ReadsOf(int processId) => _reads.GetValueOrDefault(processId);

    public ProcessElevation Read(int processId)
    {
        _reads[processId] = ReadsOf(processId) + 1;
        return _levels.GetValueOrDefault(processId, ProcessElevation.Unknown);
    }
}
