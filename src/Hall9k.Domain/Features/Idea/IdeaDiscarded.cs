namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Historical only — retired by the fan-out redesign (backlog 31) in favor of
/// <see cref="IdeaArchived"/>, the one door for setting an idea aside now. This type and its
/// <c>Apply</c> handlers stay only to replay streams that already recorded it — nothing is
/// deleted or rewritten. "Discovery found nothing to do" was always what this meant, which is
/// exactly what <see cref="IdeaState.Archived"/> means today, so replaying this event (or
/// reading a document it last wrote) lands there. Recorded with its reason, never deleted — an
/// idea that keeps coming back is exactly the signal the parking garage is for (PLAN.md §3.1).
/// </summary>
public sealed record IdeaDiscarded(
    Guid Id,
    string Reason,
    DateTimeOffset DiscardedAt,
    Guid DiscardedByOwnerId);
