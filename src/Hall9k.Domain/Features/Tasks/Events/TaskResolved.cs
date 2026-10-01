namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human closes a Failed task as Done (Decisions Log #27): the run failed, but the
/// objective was met anyway — the failure was in the machinery around the work, not in
/// the work. Reason is the human's attestation of *why* the objective counts as met;
/// it is required because an attestation without a why is a guess (the AGENTS.md
/// never-guess rule). PullRequestUrl records where the work landed, when known. Resolve
/// appends — it never rewrites or hides the failure: the stream reads added → claimed →
/// failed → resolved, and the task shows Done. Failed-only and human-only: no monitor
/// appends this (never loop on judgment, log #11).
/// <para>
/// <paramref name="OnBehalfOfOwnerRootFingerprint"/> and <paramref name="OverrideReason"/> are set
/// only when an Owner-role member did this to another owner's task through the deliberate
/// override (<c>--holder</c> with <c>--reason</c>): the root acted on behalf of, null when that
/// owner was unknown, and why. An owner's own act leaves both empty, and an event written before
/// they existed replays unchanged.
/// </para>
/// </summary>
public sealed record TaskResolved(
    Guid Id,
    string Reason,
    string? PullRequestUrl,
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId,
    string? OnBehalfOfOwnerRootFingerprint = null,
    string? OverrideReason = null);
