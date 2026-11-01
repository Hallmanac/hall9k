namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// One draft of the plan is published: by the plan's own publish step, or by hand (<paramref name="ByHand"/>), which
/// also answers any park on that draft.
/// </summary>
public sealed record PlanDraftPublished(
    Guid Id,
    Guid ProjectId,
    Guid DraftId,
    bool ByHand,
    DateTimeOffset PublishedAt);
