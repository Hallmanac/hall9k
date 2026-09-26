using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// For a node that joined a project before node files carried a GitHub declaration: once per
/// process and project, off the first message-sweep tick that reads that project, writes this
/// node's own GitHub login and account id into its existing <c>node.yaml</c>
/// (<see cref="NodeFileWriter.RefreshGitHubDeclarationAsync"/>), so existing members fill in with no
/// action. Never creates a node file, never runs inside <c>NodeContext.InitializeAsync</c>, and never
/// blocks daemon start: a failure is logged and the next daemon start tries again, and a connection
/// with no observed GitHub identity writes nothing.
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
            await using (IQuerySession session = store.QuerySession())
            {
                account = await ProjectGitHubClient.TryResolveAccountAsync(session, project, cancellationToken);
            }

            if (account is null)
            {
                logger.LogInformation(
                    "Project {ProjectId}: this install has no confirmed GitHub account, so its node file's GitHub "
                    + "declaration was not written", project.Id);
                return;
            }

            NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
                ledger, project.RepositoryPath, node.NodeId, new DeclaredGitHubAccount(account.Id, account.Login),
                identity.PublicKeyLine, identity.Committer, identity.SigningKey, cancellationToken);
            if (outcome == NodeFileRefreshOutcome.SigningKeyDiffers)
            {
                logger.LogWarning(
                    "Project {ProjectId}: this node's node file names a different public key than the one this node now "
                    + "signs with, so its GitHub declaration was not written; re-run h9k project join", project.Id);
                return;
            }

            logger.LogInformation(
                "Project {ProjectId}: GitHub declaration in this node's node file: {Outcome}", project.Id, outcome);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Project {ProjectId}: writing this node's GitHub declaration failed; it is retried at the next "
                + "daemon start", project.Id);
        }
    }
}
