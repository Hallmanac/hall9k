namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// An orchestrator window left on purpose (idea 89471598, piece 1) — <c>h9k orchestrator
/// deregister</c>, which the recipe's own restart and close steps call, or the shutdown
/// <c>h9k orchestrator register --replace</c> records for the window it is taking over from.
/// The deliberate counterpart of <see cref="OrchestratorLost"/>: this window said goodbye, that
/// one simply stopped existing.
/// </summary>
public sealed record OrchestratorShutDown(
    Guid NodeId, Guid ProjectId, string SessionName, int ProcessId, DateTimeOffset ShutDownAt);
