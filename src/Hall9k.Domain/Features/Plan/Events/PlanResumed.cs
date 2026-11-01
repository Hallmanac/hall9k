namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// <c>h9k plan resume</c>: every park on the plan is answered, the steps that parked run again, and a draft that parked
/// on rounds exhausted is granted one more round.
/// </summary>
public sealed record PlanResumed(Guid Id, Guid ProjectId, Guid ResumedByOwnerId, DateTimeOffset ResumedAt);
