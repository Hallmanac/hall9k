namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// What <see cref="OrchestratorPresenceDecider.Deregister"/> concluded, for the CLI's own closing
/// line. An enum for the same reason <see cref="OrchestratorRegistrationOutcome"/> is one: it is
/// never persisted, and the event carries the fact.
/// </summary>
public enum OrchestratorDeregistrationOutcome
{
    /// <summary>This window's own registration ended, deliberately.</summary>
    Deregistered,

    /// <summary>Nothing is registered for this project on this node — a close step run twice, or
    /// one the daemon's presence sweep already beat to the record.</summary>
    NothingRegistered,

    /// <summary>A different window holds the registration, so this one has nothing to drop.</summary>
    HeldByAnotherWindow,
}

/// <summary>
/// The shutdown a deregister produces, if any, together with why there was none.
/// <see cref="ShutDown"/> is non-<see langword="null"/> exactly when
/// <see cref="Outcome"/> is <see cref="OrchestratorDeregistrationOutcome.Deregistered"/>.
/// </summary>
public sealed record OrchestratorDeregistrationDecision(
    OrchestratorDeregistrationOutcome Outcome, OrchestratorShutDown? ShutDown);
