namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// An idea closed honestly: recorded with its reason, never deleted. A discarded idea that
/// keeps coming back is exactly the signal the parking garage is for (PLAN.md §3.1).
/// </summary>
public sealed record IdeaDiscarded(
    Guid Id,
    string Reason,
    DateTimeOffset DiscardedAt,
    Guid DiscardedByOwnerId);
