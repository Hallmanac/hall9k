using System.ComponentModel;
using System.Globalization;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Revokes a node from this owner's own fleet (idea 202383dc, T1): writes
/// <c>owners/&lt;root&gt;/revoked/&lt;node-id&gt;.yaml</c> into every non-archived project this
/// owner is registered to — the same "owner-wide, every project" shape <see cref="NodeVouchCommand"/>
/// uses. Latest of vouch or revocation wins in the ledger's own ref commit order, so a surviving
/// node undoes a bad revocation simply by vouching the same node id again afterward.
/// </summary>
public sealed class NodeRevokeCommand : Hall9kAsyncCommand<NodeRevokeCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NODE_ID>")]
        [Description("The node id to revoke — h9k status prints a node's own id.")]
        public string NodeId { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken) =>
        RunAsync(
            session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(settings.NodeId, out Guid targetNodeId))
        {
            throw new DomainValidationException($"'{settings.NodeId}' is not a node id — h9k status prints a node's own id.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        if (owner.RootFingerprint is not { } root)
        {
            throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        }

        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.OwnerId == context.OwnerId && !project.IsArchived)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            throw new DomainValidationException("No project is registered to this owner yet — run h9k project join <project> first.");
        }

        int revokedIn = 0;
        List<string> failedProjects = [];
        foreach (ProjectDetails project in projects)
        {
            try
            {
                if (await RevokeInProjectAsync(
                    ledger, chainReader, project.RepositoryPath, root, targetNodeId, key.Fingerprint, now,
                    committer, signingKey, cancellationToken))
                {
                    revokedIn++;
                }
            }
            // Same reasoning as NodeVouchCommand's identical catch: each project's own copy of the
            // owner chain is independent, so this node not being enrolled there must skip that one
            // project alone, never abort a revocation that already landed in an earlier one.
            catch (DomainValidationException exception)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not revoke in '{project.Name.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}) — skipped: not enrolled there.[/]");
            }
            // Same reasoning as NodeVouchCommand's identical catch: a push rejection, a
            // network/credential failure, or exhausted conflict retries means the revocation was
            // supposed to land here and did not — that project still trusts the revoked node, and
            // reporting overall success would hide exactly the failure a revocation's own caller
            // most needs to see (independent review finding).
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException or DomainConflictException)
            {
                failedProjects.Add(project.Name);
                AnsiConsole.MarkupLine(
                    $"[red]Failed to revoke in '{project.Name.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}) — "
                    + "this project still trusts the node; re-run once fixed.[/]");
            }
        }

        // Unlike a vouch (which can legitimately find the target simply hasn't joined a given
        // project yet), RevokeInProjectAsync only ever fails by throwing — so revokedIn == 0 means
        // every single project refused this node as unenrolled or failed outright, and there is
        // nothing real to record locally either.
        if (revokedIn == 0)
        {
            throw new DomainValidationException(
                $"Nothing was revoked for node {targetNodeId} in any project — this node is not itself "
                + "enrolled anywhere it could act, or every attempt failed outright; see the messages "
                + "above for which.");
        }

        session.Events.Append(context.OwnerId, OwnerDecider.RevokeNode(owner, targetNodeId, now));
        await session.SaveChangesAsync(cancellationToken);

        if (failedProjects.Count > 0)
        {
            throw new DomainValidationException(
                $"Revoked node {targetNodeId} in {revokedIn} project(s), but failed in {failedProjects.Count}: "
                + $"{string.Join(", ", failedProjects)} — that project still trusts this node until you re-run "
                + $"h9k node revoke {targetNodeId} once the failure is fixed.");
        }

        AnsiConsole.MarkupLine(
            $"[green]Revoked[/] node [dim]{targetNodeId}[/] from owner [dim]{root}[/]'s own fleet, "
            + $"across {revokedIn} project(s). A later h9k node vouch {targetNodeId} restores it.");
        return ExitCodes.Ok;
    }

    /// <summary>Refuses before any push under the identical rule <c>NodeVouchCommand</c> applies.
    /// Always writes a fresh commit (never write-if-absent), so the revocation lands even when a
    /// prior revocation for this node id already exists — the same reasoning as a re-vouch.</summary>
    private static async Task<bool> RevokeInProjectAsync(
        ILedger ledger, ILedgerChainReader chainReader, string repositoryPath, string root, Guid targetNodeId,
        string myFingerprint, DateTimeOffset now, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        TrustChain chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
        if (!chain.IsEnrolledInOwner(myFingerprint, root))
        {
            throw new DomainValidationException(
                $"This node ({myFingerprint}) is not currently enrolled in owner {root}'s own chain in "
                + $"'{repositoryPath}' — only the root itself or a node already vouched into it may revoke "
                + "another (idea 202383dc: \"written by any enrolled node of that owner\").");
        }

        string refName = $"refs/hall9k/ledger/owners/{root}";
        string path = $"owners/{root}/revoked/{targetNodeId}.yaml";
        string content = BuildYaml(("node_id", targetNodeId.ToString()), ("revoked_at", now.ToString("o", CultureInfo.InvariantCulture)));

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId, $"Revoke node {targetNodeId}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return true;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this revocation after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k node revoke once that settles.");
    }

    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
        }

        return builder.ToString();
    }
}
