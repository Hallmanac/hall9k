using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The daemon dispatched the refinement session for one draft. Appended in the same save as the task stream's
/// <c>DraftRefinementDispatched</c>, so the plan never claims a session the task does not know about.
/// </summary>
public sealed record PlanRefinementDispatched(
    Guid Id,
    Guid ProjectId,
    Guid DraftId,
    Guid SessionId,
    RefinementPurpose Purpose,
    DateTimeOffset DispatchedAt);
