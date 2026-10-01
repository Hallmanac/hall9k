namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Queued or Blocked -> Published: the reverse of the dispatch trigger (Decisions Log #34).
/// Refused while a lease is held — revising work a node is already running races the
/// dispatcher, which is the whole reason editing stops at Draft.
/// <para>
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/> are set
/// only when an Owner-role member did this to another owner's task through the deliberate
/// override (<c>--holder</c> with <c>--reason</c>): the root acted on behalf of, null when that
/// owner was unknown, and why. An owner's own act leaves both empty, and an event written before
/// they existed replays unchanged.
/// </para>
/// </summary>
public sealed record TaskUnassigned(
    Guid Id,
    string? Reason,
    DateTimeOffset UnassignedAt,
    Guid UnassignedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
