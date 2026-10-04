namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The assignee lets go of a Draft or Published task that is not queued: the task falls back to its
/// creator (<see cref="Handlers.TaskOwnerRule"/>). The counterpart of <see cref="TaskAssigneeSet"/>;
/// a Queued or Blocked task releases its assignee through <see cref="TaskUnassigned"/>, which clears
/// both the assignee and the queued-for owner in one event.
/// <para>
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/> follow
/// <see cref="TaskUnassigned"/>'s own convention: set only when an Owner-role member cleared another
/// owner's hold through the deliberate override (<c>--holder</c> with <c>--reason</c>).
/// </para>
/// </summary>
public sealed record TaskAssigneeCleared(
    Guid Id,
    string? Reason,
    DateTimeOffset ClearedAt,
    Guid ClearedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
