namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The plan parked for a person on one draft, for a reason from a closed list. While any draft is parked no new
/// session starts for any of the plan's drafts, and a session already running finishes. The record names the draft and
/// the reason and holds nothing a session wrote.
/// </summary>
public sealed record PlanParked(
    Guid Id,
    Guid ProjectId,
    Guid DraftId,
    PlanParkReason Reason,
    Guid? SessionId,
    DateTimeOffset ParkedAt);
