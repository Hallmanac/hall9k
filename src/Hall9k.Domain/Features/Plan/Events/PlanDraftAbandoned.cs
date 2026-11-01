namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>One draft of the plan was abandoned by hand, which also answers any park on it.</summary>
public sealed record PlanDraftAbandoned(Guid Id, Guid ProjectId, Guid DraftId, DateTimeOffset AbandonedAt);
