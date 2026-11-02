namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The daemon dispatched the discovery session of a plan started from an idea. There is no task to carry the dispatch,
/// so it rides the plan's own stream alone, recorded before anything is spawned so a crash loses the session rather than
/// running one the record does not know about.
/// </summary>
public sealed record PlanDiscoveryDispatched(Guid Id, Guid ProjectId, Guid IdeaId, Guid SessionId, DateTimeOffset DispatchedAt);
