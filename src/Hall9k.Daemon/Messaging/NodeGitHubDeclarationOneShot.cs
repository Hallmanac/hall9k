using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// For a node whose existing <c>node.yaml</c> is stale on either fact this node itself declares:
/// once per process and project, off the first message-sweep tick that reads that project, brings
/// this node's own GitHub login and account id (<see cref="NodeFileWriter.RefreshGitHubDeclarationAsync"/>)
/// and its display name (<see cref="NodeFileWriter.RefreshDisplayNameAsync"/>, task e6744304) up to
/// their current effective values in the same pass, so existing members fill in with no action.
/// Never creates a node file, never runs inside <c>NodeContext.InitializeAsync</c>, and never blocks
/// daemon start: a failure is logged and the next daemon start tries again, and a connection with no
/// observed GitHub identity simply skips that one field.
/// </summary>
public sealed class NodeGitHubDeclarationOneShot(
    IDocumentStore store, NodeContext node, ILedger ledger, ILogger<NodeGitHubDeclarationOneShot> logger)
{
    private readonly HashSet<Guid> attemptedProjectIds = [];

    public async Task RunOnceAsync(ProjectDetails project, MessageNodeIdentity identity, CancellationToken cancellationToken)
    {
        if (!attemptedProjectIds.Add(project.Id))
        {
            return;
        }

        try
        {
            ProjectGitHubAccount? account;
            DisplayName effectiveDisplayName;
            await using (IQuerySession session = store.QuerySession())
            {
                account = await ProjectGitHubClient.TryResolveAccountAsync(session, project, cancellationToken);
                OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(node.OwnerId, cancellationToken);
                effectiveDisplayName = owner?.EffectiveDisplayName(project.Id) ?? DisplayName.None;
            }

            if (account is null)
            {
                logger.LogInformation(
                    "Project {ProjectId}: this install has no confirmed GitHub account, so its node file's GitHub "
                    + "declaration was not written", project.Id);
            }
            else
            {
                NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
                    ledger, project.RepositoryPath, node.NodeId, new DeclaredGitHubAccount(account.Id, account.Login),
                    identity.PublicKeyLine, identity.Committer, identity.SigningKey, cancellationToken);
                if (outcome == NodeFileRefreshOutcome.SigningKeyDiffers)
                {
                    logger.LogWarning(
                        "Project {ProjectId}: this node's node file names a different public key than the one this "
                        + "node now signs with, so its GitHub declaration was not written; re-run h9k project join",
                        project.Id);
                }
                else
                {
                    logger.LogInformation(
                        "Project {ProjectId}: GitHub declaration in this node's node file: {Outcome}", project.Id, outcome);
                }
            }

            NodeFileRefreshOutcome displayNameOutcome = await NodeFileWriter.RefreshDisplayNameAsync(
                ledger, project.RepositoryPath, node.NodeId, effectiveDisplayName, identity.PublicKeyLine,
                identity.Committer, identity.SigningKey, cancellationToken);
            if (displayNameOutcome == NodeFileRefreshOutcome.SigningKeyDiffers)
            {
                logger.LogWarning(
                    "Project {ProjectId}: this node's node file names a different public key than the one this "
                    + "node now signs with, so its display name was not written; re-run h9k project join", project.Id);
            }
            else
            {
                logger.LogInformation(
                    "Project {ProjectId}: display name in this node's node file: {Outcome}", project.Id, displayNameOutcome);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Project {ProjectId}: writing this node's facts failed; it is retried at the next "
                + "daemon start", project.Id);
        }
    }
}
