using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The plan parked in discovery, which has no draft to park on: the report landed and waits at the gate
/// (<see cref="PlanParkReason.Gate"/>), or the session failed (<see cref="PlanParkReason.SessionFailed"/>, with the closed
/// reason it failed for). The record names the idea, the reason, and the session, and holds nothing the session wrote.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project, read by the orchestrator feed from the event itself.</param>
/// <param name="IdeaId">The idea the plan is running discovery on.</param>
/// <param name="Reason">Gate or session failed.</param>
/// <param name="SessionId">The session whose report this park is about, or null when none ran (a dispatch refused before it started).</param>
/// <param name="Failure">Why the session failed, from the closed list; Unknown at the gate.</param>
/// <param name="ParkedAt">When.</param>
public sealed record PlanDiscoveryParked(
    Guid Id,
    Guid ProjectId,
    Guid IdeaId,
    PlanParkReason Reason,
    Guid? SessionId,
    RefinementFailureReason Failure,
    DateTimeOffset ParkedAt);
