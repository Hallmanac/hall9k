using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// For a root node whose already-vouched fleet predates this task's succession model (idea
/// 6be68ee2), or whose fleet has since grown a node no successor record yet counts for: every
/// message-sweep tick, off the same trust chain that tick already computed, writes a root-signed
/// successor record for every currently vouched node of this root's own fleet that does not already
/// have one counting on read — the backfill named in the origin verdict ("the Mac and Windows
/// today; no re-join"), kept self-healing rather than run once per process (independent pre-PR
/// review, cycle 1, conformance lens, medium: a project this root's own daemon had not yet joined
/// at the process's first sweep tick — a sibling that joins it later, say — never got a second
/// chance at this backfill until the next daemon restart). Gated on
/// <see cref="TrustedOwner.SuccessorNodeIds"/> and <see cref="TrustedOwner.RootKeys"/> — both
/// already derived from this same trust chain — rather than a raw ledger read of the successor
/// file's own existence: a node whose only successor record was signed by someone other than a live
/// root key (an ordinary fleet node's own vouch, say) has a file that exists but never counts, and a
/// raw existence check left it stuck that way forever (independent pre-PR review, cycle 1, both
/// lenses, medium). Does nothing when this node's own key is not currently one of its root's own
/// live keys (idea 6be68ee2's ranked root-key set): only a root can ever write a successor record
/// that counts, so an ordinary fleet node has nothing to backfill. Never blocks the sweep, and a
/// failure is logged and retried next tick.
/// </summary>
public sealed class NodeSuccessorBackfillReconciler(ILedger ledger, ILogger<NodeSuccessorBackfillReconciler> logger)
{
    public async Task ReconcileAsync(
        ProjectDetails project, MessageNodeIdentity identity, TrustChain trustChain, CancellationToken cancellationToken)
    {
        try
        {
            if (!trustChain.OwnerChains.TryGetValue(identity.OwnerRootFingerprint, out TrustedOwner? owner))
            {
                return;
            }

            string myFingerprint = NodeKeyStore.Fingerprint(identity.PublicKeyLine);
            if (!owner.IsLiveRootKey(myFingerprint))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            int backfilled = 0;
            foreach (TrustedNode fleetNode in owner.Nodes)
            {
                if (!Guid.TryParse(fleetNode.NodeId, out Guid nodeId))
                {
                    continue;
                }

                bool alreadyCounts = owner.SuccessorNodeIds.Contains(fleetNode.NodeId)
                    || owner.RootKeys.Any(rootKey => rootKey.IntroducedByNodeId == fleetNode.NodeId);
                if (alreadyCounts)
                {
                    continue;
                }

                await SuccessionLedgerWriter.WriteSuccessorAsync(
                    ledger, project.RepositoryPath, identity.OwnerRootFingerprint, nodeId, fleetNode.PublicKeyLine, now,
                    identity.Committer, identity.SigningKey, cancellationToken);
                backfilled++;
            }

            if (backfilled > 0)
            {
                logger.LogInformation(
                    "Project {ProjectId}: backfilled {Count} successor record(s) for this root's already-vouched fleet",
                    project.Id, backfilled);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Project {ProjectId}: this root's successor backfill failed; it is retried next sweep",
                project.Id);
        }
    }
}
