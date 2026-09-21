namespace Hall9k.Connectors.Replication;

/// <summary>
/// What one sweep decided about the streams this node holds only the tail of
/// (<see cref="EventCatchUpCoordinator.PlanHeldTailAsks"/>): the ones it asks the project for, and
/// the ones it has stopped asking about. Two lists rather than one, because the second is not the
/// complement of the first — a stream still settling, still waiting on an outstanding ask, or
/// still inside its cooldown is in neither, and saying so is the difference between "nobody can
/// complete this" and "nobody has answered yet".
/// </summary>
/// <param name="ToAsk">
/// Streams earning a broadcast stream request this sweep, longest-held first, capped at
/// <see cref="EventCatchUpCoordinator.MaxHeldTailAsksPerSweep"/>.
/// </param>
/// <param name="ToGiveUp">
/// Streams that reached <see cref="EventCatchUpCoordinator.MaxHeldTailAttempts"/> and whose last
/// ask has since gone unanswered past its cooldown — the moment the verdict is honest, which is
/// not the moment the last ask went out.
/// </param>
public sealed record HeldTailAskPlan(IReadOnlyList<Guid> ToAsk, IReadOnlyList<Guid> ToGiveUp);
