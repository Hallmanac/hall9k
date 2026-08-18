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
/// </summary>
public sealed record TaskResolved(
    Guid Id,
    string Reason,
    string? PullRequestUrl,
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId);
