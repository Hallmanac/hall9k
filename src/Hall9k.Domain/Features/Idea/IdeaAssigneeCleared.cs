namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The assignee lets go of an idea: it falls back to its creator
/// (<see cref="Tasks.Handlers.TaskOwnerRule"/>). The counterpart of <see cref="IdeaAssigneeSet"/>.
/// <para>
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/> follow
/// <see cref="Tasks.Events.TaskAssigneeCleared"/>'s own convention: set only when an Owner-role
/// member cleared another owner's hold through the deliberate override (<c>--holder</c> with
/// <c>--reason</c>).
/// </para>
/// </summary>
public sealed record IdeaAssigneeCleared(
    Guid Id,
    string? Reason,
    DateTimeOffset ClearedAt,
    Guid ClearedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
