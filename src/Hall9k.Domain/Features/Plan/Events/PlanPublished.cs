namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The plan's end: its last draft is published. Reaches the orchestrator feed as an ordinary item.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="PublishedAt">When the plan ended.</param>
/// <param name="WithoutTrackerItem">
/// The published drafts that, on a project tracking a backlog, had no tracker item and none on its way when the plan ended and
/// were not published untracked. Ids only, so the end item can say so without carrying any text.
/// </param>
public sealed record PlanPublished(Guid Id, Guid ProjectId, DateTimeOffset PublishedAt, IReadOnlyList<Guid> WithoutTrackerItem);
