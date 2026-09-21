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
/// <para>
/// Deliberately named <c>NotJoined</c> rather than <c>NeedsInvite</c>: this local mirror cannot
/// tell "someone else already owns this project and a real invite is needed" apart from "this
/// install is this project's own owner and simply has not run its own join yet" (h9k project add's
/// best-effort join is skipped when the repository is not yet reachable, or fails). Only the live
/// ledger read inside <c>h9k project join</c> itself can tell the two apart — this projection just
/// flags that the project is not fully joined and points at the command that will (independent
/// pre-PR review, cycle 1, conformance and adversarial lenses).
/// </para>
/// </summary>
public static class ProjectJoinStatus
{
    /// <summary>The pure check, for a caller that already has this owner's own root fingerprint on
    /// hand (<c>h9k project show</c> already loads <see cref="OwnerDetails"/> for its own Owner row)
    /// rather than paying for a second lookup <see cref="NotJoinedAsync"/> would otherwise repeat.</summary>
    public static bool NotJoined(ProjectDetails project, string? ownerRootFingerprint) =>
        ownerRootFingerprint is null || !project.Members.ContainsKey(ownerRootFingerprint);

    public static async Task<bool> NotJoinedAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken)
    {
        string? rootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, project.OwnerId, cancellationToken);
        return NotJoined(project, rootFingerprint);
    }
}
