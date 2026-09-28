using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// For a root node whose already-vouched fleet predates this task's succession model (idea
/// 6be68ee2): once per process and project, off the same first message-sweep tick
/// <see cref="NodeGitHubDeclarationOneShot"/> already rides, writes a root-signed successor record
/// for every currently vouched node of this root's own fleet that does not already have one — the
/// backfill named in the origin verdict ("the Mac and Windows today; no re-join"). Does nothing when
/// this node's own key is not currently one of its root's own live keys (idea 6be68ee2's ranked
/// root-key set): only a root can ever write a successor record that counts, so an ordinary fleet
/// node has nothing to backfill. Never blocks daemon start, and a failure is logged and retried at
/// the next daemon start, the identical posture <see cref="NodeGitHubDeclarationOneShot"/> already
/// takes.
/// </summary>
public sealed class NodeSuccessorBackfillOneShot(ILedger ledger, ILogger<NodeSuccessorBackfillOneShot> logger)
{
    private readonly HashSet<Guid> attemptedProjectIds = [];

    public async Task RunOnceAsync(
        ProjectDetails project, MessageNodeIdentity identity, TrustChain trustChain, CancellationToken cancellationToken)
    {
        if (!attemptedProjectIds.Add(project.Id))
        {
            return;
        }

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
            string refName = $"refs/hall9k/ledger/owners/{identity.OwnerRootFingerprint}";
            int backfilled = 0;
            foreach (TrustedNode fleetNode in owner.Nodes)
            {
                if (!Guid.TryParse(fleetNode.NodeId, out Guid nodeId))
                {
                    continue;
                }

                string path = $"owners/{identity.OwnerRootFingerprint}/successors/{fleetNode.NodeId}.yaml";
                LedgerFile existing = await ledger.ReadAsync(project.RepositoryPath, refName, path, cancellationToken);
                if (existing.Exists)
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
                exception, "Project {ProjectId}: this root's successor backfill failed; it is retried at the next daemon start",
                project.Id);
        }
    }
}
