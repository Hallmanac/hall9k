namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// Somebody asked for this project's run skill to be discovered (idea b9b09779, piece 4): either
/// <c>h9k project add</c> at registration, or <c>h9k project set --discover-run-skill</c> on a
/// project that already exists. The ask alone, never the answer — the daemon's own
/// <c>RunSkillSweepEngine</c> is what surveys the repository and, when there is something to read,
/// dispatches the discovery session.
/// <para>
/// A second request while an earlier one is still outstanding is not an error: it supersedes,
/// which is what makes <c>--discover-run-skill</c> the honest way to redo a discovery whose
/// session died or whose repository has since changed.
/// </para>
/// </summary>
public sealed record ProjectRunSkillDiscoveryRequested(
    Guid ProjectId,
    DateTimeOffset RequestedAt,
    Guid RequestedByOwnerId);
