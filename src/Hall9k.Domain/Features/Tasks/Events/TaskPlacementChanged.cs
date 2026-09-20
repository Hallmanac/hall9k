namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human changed an already-assigned task's advisory node placement without touching anything
/// else about it (idea 202383dc: an owner can place a task on one of their own nodes) —
/// <c>h9k task assign &lt;id&gt; --node</c> run against a task past <see cref="TaskState.Published"/>,
/// where <see cref="Handlers.TaskDecider.Assign"/> itself cannot run again without an
/// <c>h9k task unassign</c> first. Unlike <see cref="TaskAssigned.PlacedOnNodeId"/>, this carries a
/// plain nullable node id rather than an <c>Optional</c>: the whole reason this event exists is to
/// change the placement, so there is no "leave it alone" case to distinguish from "clear it" — null
/// always means clear, a value always means pin.
/// </summary>
public sealed record TaskPlacementChanged(Guid Id, Guid? PlacedOnNodeId, DateTimeOffset ChangedAt, Guid ChangedByOwnerId);
