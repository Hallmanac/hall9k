namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// A registered orchestrator window whose process is no longer on this node's process table
/// (idea 89471598, piece 1), recorded by the daemon's own presence sweep rather than by anything
/// the window did — a closed terminal, a killed process, a machine that went to sleep and came
/// back with the pid gone. The honest counterpart of <see cref="OrchestratorShutDown"/>: the
/// window never said goodbye, so the time carried here is when the sweep first noticed, never a
/// guess at when it actually died.
/// </summary>
public sealed record OrchestratorLost(
    Guid NodeId, Guid ProjectId, string SessionName, int ProcessId, DateTimeOffset LostAt);
