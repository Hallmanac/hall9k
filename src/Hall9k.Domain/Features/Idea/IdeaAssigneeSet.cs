namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// A member lays hold of an idea, visibly to the fleet or the team, before any task exists: the idea's
/// assignee (see <see cref="IdeaAggregate.AssigneeOwnerId"/>) becomes <see cref="AssigneeOwnerId"/>.
/// Also the hand-off: the current assignee, or the creator when there is none, may name another
/// member here directly (a request-and-approve hand-off is a separate idea). Only the assignee, or
/// the creator when there is none, decides the idea's fate (<c>h9k idea conclude</c>, <c>archive</c>
/// and <c>promote</c>), by <see cref="Tasks.Handlers.TaskOwnerRule"/> over verified owner roots.
/// <para>
/// Not <see cref="IdeaAssignedToProject"/>: that event moves an idea between projects and keeps
/// meaning exactly that. A node on an older build does not know this type, skips it and keeps no
/// assignee for the idea (the creator fallback), and has no idea gate at all, so it applies a
/// conclude or archive that upgraded peers refuse. Every node of every member must update before
/// anyone assigns an idea to another member.
/// </para>
/// </summary>
/// <param name="AssigneeOwnerRootFingerprint">
/// The assignee's cross-node root fingerprint beside <see cref="AssigneeOwnerId"/>'s local Guid, the
/// same pairing <see cref="Tasks.Events.TaskAssigneeSet"/> carries: the receive gate compares roots,
/// never Guids, because Owner events never replicate. Null only when the assigning node's own record
/// of that owner has no root yet.
/// </param>
/// <param name="OnBehalfOfOwnerRootFingerprint">
/// Set, with <paramref name="OverrideReason"/>, only when an Owner-role member laid this on an idea
/// another owner held, through the deliberate override (<c>--holder</c> with <c>--reason</c>), by
/// <see cref="Tasks.Events.TaskAssigneeSet"/>'s own convention. An owner's own act leaves both empty.
/// </param>
public sealed record IdeaAssigneeSet(
    Guid Id,
    Guid AssigneeOwnerId,
    string? AssigneeOwnerRootFingerprint,
    DateTimeOffset SetAt,
    Guid SetByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
