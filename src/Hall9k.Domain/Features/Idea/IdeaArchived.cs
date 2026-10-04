namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// An idea's other terminal act, always an explicit human decision (Brian, 2026-08-21):
/// discovery happened and nothing came of it. Recorded with its reason, never deleted — an
/// archived idea that keeps coming back is exactly the signal the parking garage is for
/// (PLAN.md §3.1).
/// <para>
/// Reconciles the shipped <c>IdeaDiscarded</c> (backlog 22): "discovery found nothing to do" is
/// this same case, with the reason carrying what was learned. One door, honestly renamed —
/// <c>h9k idea archive</c> replaces <c>h9k idea discard</c> outright rather than standing beside
/// it as a second name for the same act.
/// </para>
/// <para>
/// Decided by the same rule as <see cref="IdeaConcluded"/>: the idea's assignee, or its creator when
/// it has none, or an Owner-role member through the override, which fills
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/>.
/// </para>
/// </summary>
public sealed record IdeaArchived(
    Guid Id,
    string Reason,
    DateTimeOffset ArchivedAt,
    Guid ArchivedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
