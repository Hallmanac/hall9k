namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A member lays hold of a Draft or Published task without queueing it: the task's assignee (see
/// <see cref="TaskAggregate.AssigneeOwnerId"/>) becomes <see cref="AssigneeOwnerId"/> and nothing
/// else about the task moves, so no dispatcher ever sees this event act. Also the hand-off: the
/// current assignee may name another member here directly (a request-and-approve hand-off is a
/// separate idea). Never written for a Queued or later task, where the assignee is
/// <see cref="TaskAggregate.AssignedOwnerId"/> by construction and only
/// <see cref="TaskUnassigned"/> releases it.
/// <para>
/// Not <see cref="TaskAssigned"/>: that event stays the go signal on the wire, with every historical
/// one replaying to Queued, so an older node that does not know this type skips it and keeps the
/// creator fallback for the task (fails closed), where reusing <see cref="TaskAssigned"/> would
/// have queued the task on that node (fails open). Every node of every member must update before
/// anyone assigns or hands off to another member.
/// </para>
/// </summary>
/// <param name="AssigneeOwnerRootFingerprint">
/// The assignee's cross-node root fingerprint beside <see cref="AssigneeOwnerId"/>'s local Guid,
/// the same pairing <see cref="TaskAssigned.AssignedOwnerRootFingerprint"/> carries: the receive
/// gate and <see cref="Handlers.TaskOwnerRule"/> compare roots, never Guids, because Owner events
/// never replicate. Null only when the assigning node's own record of that owner has no root yet.
/// </param>
/// <param name="OnBehalfOfOwnerRootFingerprint">
/// Set, with <paramref name="OverrideReason"/>, only when an Owner-role member laid this on a task
/// another owner held, through the deliberate override (<c>--holder</c> with <c>--reason</c>), by
/// <see cref="TaskUnassigned"/>'s own convention. An owner's own act leaves both empty.
/// </param>
public sealed record TaskAssigneeSet(
    Guid Id,
    Guid AssigneeOwnerId,
    string? AssigneeOwnerRootFingerprint,
    DateTimeOffset SetAt,
    Guid SetByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
