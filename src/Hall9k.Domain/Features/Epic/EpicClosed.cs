namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// An epic closed by an explicit human act, with a reason. Nothing else ever closes an epic —
/// not its last member task closing out — the same never-auto-close doctrine a task's
/// terminal states already carry.
/// </summary>
public sealed record EpicClosed(
    Guid Id,
    string Reason,
    DateTimeOffset ClosedAt,
    Guid ClosedByOwnerId);
