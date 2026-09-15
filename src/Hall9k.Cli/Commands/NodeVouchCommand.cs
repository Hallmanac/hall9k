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
/// Vouches a node into this owner's own fleet (idea 202383dc, T1): writes
/// <c>owners/&lt;root&gt;/nodes/&lt;node-id&gt;.yaml</c> — node id, node public key, issued at —
/// into every non-archived project this owner is registered to, the same "owner-wide, every
/// project" shape <c>h9k project join</c>'s own root retirement already uses. Refused, before any
/// push, when this node itself is not currently enrolled in that owner's own chain (the root's own
/// key, or a node already vouched and not revoked) — the rule idea 202383dc's own model states:
/// "written by any enrolled node of that owner."
/// </summary>
public sealed class NodeVouchCommand : Hall9kAsyncCommand<NodeVouchCommand.Settings>
{
    /// <summary>How many times a conflicting ledger write retries — same bound <c>ProjectJoinCommand</c> uses.</summary>
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NODE_ID>")]
        [Description("The node id to vouch for — h9k status prints a node's own id.")]
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

    /// <summary>The whole vouch flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
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

        int vouchedInto = 0;
        string? targetFingerprint = null;
        foreach (ProjectDetails project in projects)
        {
            try
            {
                string? fingerprint = await VouchInProjectAsync(
                    ledger, chainReader, project.RepositoryPath, root, targetNodeId, key.Fingerprint, now,
                    committer, signingKey, cancellationToken);
                if (fingerprint is not null)
                {
                    vouchedInto++;
                    targetFingerprint ??= fingerprint;
                }
            }
            // A per-project refusal (this node not yet enrolled in THAT project's own copy of the
            // owner chain — each project's ledger is independent) is a reason to skip that one
            // project, never to abort a vouch that already landed in an earlier one: a
            // DomainValidationException left uncaught here would otherwise throw partway through
            // the loop, leaving an already-successful write unreported and its own
            // OwnerDecider.VouchNode event never appended (blast-radius sweep finding).
            // DomainConflictException — VouchInProjectAsync's own retries exhausted — belongs in
            // this same set for the identical reason: it is thrown by that per-project call, not
            // this loop, so letting it escape here would abort every later project's own vouch too
            // (independent pre-PR review, cycle 1, conformance and adversarial lenses, low).
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException
                    or DomainValidationException or DomainConflictException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not vouch in '{project.Name.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}) — skipped.[/]");
            }
        }

        if (vouchedInto == 0 || targetFingerprint is null)
        {
            throw new DomainValidationException(
                $"Nothing was vouched for node {targetNodeId} in any project — either it has not joined one "
                + "yet (its own node file has to exist somewhere first, h9k project join on that node), or "
                + "this node is not itself enrolled anywhere it did join; see the messages above for which.");
        }

        session.Events.Append(context.OwnerId, OwnerDecider.VouchNode(owner, targetNodeId, targetFingerprint, now));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Vouched[/] node [dim]{targetNodeId}[/] [dim](key fingerprint {targetFingerprint})[/] into "
            + $"owner [dim]{root}[/]'s own fleet, across {vouchedInto} project(s).");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Refuses before any push when this node's own key is not currently enrolled in
    /// <paramref name="root"/>'s chain — the root's own key, or itself an already-vouched,
    /// not-revoked node. Returns null, without writing, when the target has no node file in this
    /// project yet (nothing here to vouch); otherwise the target's own key fingerprint, once the
    /// vouch file lands — always as a fresh commit, even when its content happens to be unchanged
    /// from before, since a surviving node's re-vouch after a bad revocation only wins by landing
    /// later in the ref's own commit order, not by differing in content.
    /// </summary>
    private static async Task<string?> VouchInProjectAsync(
        ILedger ledger, ILedgerChainReader chainReader, string repositoryPath, string root, Guid targetNodeId,
        string myFingerprint, DateTimeOffset now, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        TrustChain chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
        if (!chain.IsEnrolledInOwner(myFingerprint, root))
        {
            throw new DomainValidationException(
                $"This node ({myFingerprint}) is not currently enrolled in owner {root}'s own chain in "
                + $"'{repositoryPath}' — only the root itself or a node already vouched into it may vouch "
                + "another (idea 202383dc: \"written by any enrolled node of that owner\"). Ask an "
                + "already-enrolled node to vouch this one first.");
        }

        string targetNodeRefName = $"refs/hall9k/ledger/nodes/{targetNodeId}";
        string targetNodePath = $"nodes/{targetNodeId}/node.yaml";
        LedgerFile targetNodeFile = await ledger.ReadAsync(repositoryPath, targetNodeRefName, targetNodePath, cancellationToken);
        if (!targetNodeFile.Exists)
        {
            return null;
        }

        string? targetPublicKey = ExtractQuotedYamlValue(targetNodeFile.Content!, "public_key");
        if (targetPublicKey is null)
        {
            return null;
        }

        string refName = $"refs/hall9k/ledger/owners/{root}";
        string path = $"owners/{root}/nodes/{targetNodeId}.yaml";
        string content = BuildYaml(
            ("node_id", targetNodeId.ToString()),
            ("public_key", targetPublicKey),
            ("issued_at", now.ToString("o", CultureInfo.InvariantCulture)));

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId, $"Vouch node {targetNodeId}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return NodeKeyStore.Fingerprint(targetPublicKey);
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this vouch after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k node vouch once that settles.");
    }

    /// <summary>Reverses <see cref="BuildYaml"/>'s own shape — reads back the target node's own
    /// declared public key from its self-announced node file. Duplicated from
    /// <c>GitLedgerChainReader.ExtractQuotedYamlValue</c> rather than shared across the assembly
    /// boundary: a tiny, already-proven pure string function, the same reasoning
    /// <c>GitLedgerMessageTransport</c>'s own doc comment gives for not sharing its private
    /// process runner.</summary>
    private static string? ExtractQuotedYamlValue(string yaml, string key)
    {
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string prefix = $"{key}: \"";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('"'))
            {
                continue;
            }

            string inner = line[prefix.Length..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return null;
    }

    /// <summary>Mirrors <c>ProjectJoinCommand</c>'s own small, flat, every-value-double-quoted YAML shape.</summary>
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
