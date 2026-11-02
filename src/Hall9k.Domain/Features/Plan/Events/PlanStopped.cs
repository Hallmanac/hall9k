namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// <c>h9k plan stop</c>: a person answered the gate by not pursuing the idea. A normal ending, not a failure or a cancellation:
/// the idea stays Captured with its workspace, its report, and any tasks already cut from it untouched.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="IdeaId">The idea the plan ran discovery on.</param>
/// <param name="Reason">Why, in the person's own words.</param>
/// <param name="StoppedByOwnerId">Who stopped it.</param>
/// <param name="StoppedAt">When the plan ended.</param>
public sealed record PlanStopped(Guid Id, Guid ProjectId, Guid IdeaId, string Reason, Guid StoppedByOwnerId, DateTimeOffset StoppedAt);
