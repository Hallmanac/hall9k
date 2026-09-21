using Hall9k.Domain.Features.Owner;
using Marten;

namespace Hall9k.Domain.Features.Project.Projections;

/// <summary>
/// Whether this install is a recognized member of a project's own ledger yet (task: a newcomer who
/// registers a project whose ledger already has an owner is told so and asked for their invite
/// token). Read from purely local, already-replicated state — this install's own claimed root
/// fingerprint (<see cref="OwnerRootFingerprintResolver"/>) and this project's own local mirror of
/// ledger membership (<see cref="ProjectDetails.Members"/>, kept current by
/// <c>ProjectStreamReplicationRules.IsProjectAggregateStreamEvent</c>'s own <c>MemberVouched</c>
/// replication) — never a live ledger fetch: <c>h9k project show</c> and <c>h9k status</c> both read
/// this on every run, and neither pays for a network round trip just to answer it.
/// </summary>
public static class ProjectJoinStatus
{
    /// <summary>The pure check, for a caller that already has this owner's own root fingerprint on
    /// hand (<c>h9k project show</c> already loads <see cref="OwnerDetails"/> for its own Owner row)
    /// rather than paying for a second lookup <see cref="NeedsInviteAsync"/> would otherwise repeat.</summary>
    public static bool NeedsInvite(ProjectDetails project, string? ownerRootFingerprint) =>
        ownerRootFingerprint is null || !project.Members.ContainsKey(ownerRootFingerprint);

    public static async Task<bool> NeedsInviteAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken)
    {
        string? rootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, project.OwnerId, cancellationToken);
        return NeedsInvite(project, rootFingerprint);
    }
}
