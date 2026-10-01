namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The walk-away ending of a task, from any non-terminal state.
/// <para>
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/> are set
/// only when an Owner-role member did this to another owner's task through the deliberate
/// override (<c>--holder</c> with <c>--reason</c>): the root acted on behalf of, null when that
/// owner was unknown, and why. An owner's own act leaves both empty, and an event written before
/// they existed replays unchanged.
/// </para>
/// </summary>
public sealed record TaskAbandoned(
    Guid Id,
    string? Reason,
    DateTimeOffset AbandonedAt,
    Guid AbandonedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
