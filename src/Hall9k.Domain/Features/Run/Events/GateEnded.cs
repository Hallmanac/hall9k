namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The gate <see cref="GateStarted"/> last named has finished — passed, failed, or was killed
/// after its own timeout — so nothing is running for the display to observe until the next
/// <see cref="GateStarted"/>. Appended only once this run's own process for that gate is actually
/// confirmed done (never on the path where the daemon itself is shutting down mid-gate and never
/// waited for or killed it), so a run whose gate process is still genuinely unaccounted for keeps
/// reading as attached to it rather than as idle.
/// </summary>
public sealed record GateEnded(Guid Id, DateTimeOffset EndedAt);
