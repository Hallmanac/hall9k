namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// What <see cref="CourierGate.Decide"/> concluded about one project's own tick, for the sweep's
/// own log line and its unit tests — never persisted, the same "an in-process outcome is the one
/// thing an enum is for here" idiom <c>CardPublicationEngine.PublicationAttempt</c> already
/// follows.
/// </summary>
public enum CourierSpawnDecision
{
    /// <summary>The feed has nothing undrained for this level; nothing to deliver.</summary>
    NoItems,

    /// <summary>No orchestrator is live for this project on this node (piece 1); a courier would find nobody.</summary>
    NoOrchestrator,

    /// <summary>A courier for this project is already running; never two at once.</summary>
    AlreadyRunning,

    /// <summary>A manual `h9k orchestrator feed --drain` currently holds this project's short lease.</summary>
    ManualDrainInProgress,

    /// <summary>This project's per-day spawn cap is spent.</summary>
    DayCapReached,

    /// <summary>The batching wait has not elapsed since the last courier for this project, and nothing pending is urgent.</summary>
    Waiting,

    /// <summary>Every condition holds — spawn one.</summary>
    Spawn,
}
