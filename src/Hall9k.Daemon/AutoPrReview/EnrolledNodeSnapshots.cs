using System.Collections.Concurrent;
using Hall9k.Connectors.Trust;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// The enrolled, unrevoked nodes of this node's own owner as the message sweep last computed them
/// for each project, held in process so <see cref="AutoPrReviewEngine"/> can rank this node against
/// its fleet without a git fetch of its own. The message sweep already computes the trust chain for
/// every eligible project every 15 to 45 seconds and never persists it; it writes here, the
/// engine reads here, and nothing else does.
/// <para>
/// No entry means "this node has no fleet to defer to": the sweep has not run yet, the chain could
/// not be read, or the chain does not name this node's owner. Every one of those reads as leader,
/// which is what a single-node install has always done. A failed chain read leaves the previous
/// snapshot standing rather than erasing it, since the most recently computed chain is still the
/// best evidence of who the fleet is.
/// </para>
/// </summary>
public sealed class EnrolledNodeSnapshots
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyCollection<Guid>> _byProject = [];

    /// <summary>
    /// The owner's fleet in <paramref name="chain"/>: <see cref="TrustedOwner.FleetNodeIds"/>, which
    /// counts the root's own node (it has no vouch entry of its own) and leaves out every revoked
    /// one. Null when the chain does not contain this owner at all.
    /// </summary>
    public static IReadOnlyCollection<Guid>? FleetOf(TrustChain chain, string ownerRootFingerprint) =>
        chain.OwnerChains.TryGetValue(ownerRootFingerprint, out TrustedOwner? owner)
            ? [.. owner.FleetNodeIds()]
            : null;

    /// <summary>Records the chain the message sweep just computed for <paramref name="projectId"/>.</summary>
    public void Record(Guid projectId, TrustChain chain, string ownerRootFingerprint)
    {
        if (FleetOf(chain, ownerRootFingerprint) is { } fleet)
        {
            _byProject[projectId] = fleet;
        }
        else
        {
            _byProject.TryRemove(projectId, out _);
        }
    }

    /// <summary>The last recorded fleet for the project, or null when there is none.</summary>
    public IReadOnlyCollection<Guid>? TryGet(Guid projectId) =>
        _byProject.TryGetValue(projectId, out IReadOnlyCollection<Guid>? fleet) ? fleet : null;
}
