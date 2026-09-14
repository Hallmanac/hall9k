using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed partial class ProjectJoinCommand : Hall9kAsyncCommand<ProjectJoinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--owner <FINGERPRINT>")]
        [Description(
            "Claim an existing root's fingerprint as this node's owner instead of establishing a new "
            + "one. Recorded on the node file and the local owner record unverified, until the team "
            + "half's vouch confirms it (not yet built). Omit it on a genesis node: the first join "
            + "with no --owner establishes this owner's root using this node's own key. Re-runnable: "
            + "joining again with a different fingerprint changes the claim, retiring a self-created "
            + "root when this node turns out to belong to another one.")]
        public string? Owner { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        JoinOutcome outcome = await RunAsync(session, project, settings.Owner, cancellationToken);
        Report(project, outcome);
        return ExitCodes.Ok;
    }

    /// <summary>What one join produced, for both the command's own report and h9k project add's condensed one.</summary>
    internal sealed record JoinOutcome(
        Guid NodeId,
        string KeyFingerprint,
        string PrivateKeyPath,
        string ClaimedOwnerFingerprint,
        bool EstablishedRoot,
        bool RetiredPreviousRoot,
        bool WroteNodeFile);

    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride,
            new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new NodeKeyStore(), cancellationToken);

    /// <summary>
    /// The whole join flow, seamed on <see cref="ILedger"/> and <see cref="NodeKeyStore"/> so a
    /// test drives it against the in-memory ledger fake and a stubbed key generator rather than a
    /// real repository or a real ssh-keygen invocation (Brian's 2026-09-13 testing rule).
    /// </summary>
    internal static async Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        ILedger ledger,
        NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        if (claimedOwnerOverride.IsNotBlank() && !FingerprintPattern().IsMatch(claimedOwnerOverride))
        {
            throw new DomainValidationException(
                $"'{claimedOwnerOverride}' is not a fingerprint h9k project join can claim — a fingerprint "
                + "is the 64-character lowercase hex SHA-256 h9k owner show prints for a root, not "
                + "something typed by hand. Check the value with the owner who ran h9k owner show on the "
                + "genesis node.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        // Flushed before aggregating: on a brand-new install this is the same call that just
        // started the Owner and Node streams, and live aggregation reads the database, not this
        // session's own not-yet-saved pending events.
        await session.SaveChangesAsync(cancellationToken);

        NodeAggregate node = await session.Events.AggregateStreamAsync<NodeAggregate>(context.NodeId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No node {context.NodeId}.");
        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");

        // Signing is mandatory from here on: nothing below ever calls ILedger.WriteAsync without
        // this key, and a node that cannot produce one is refused right here, naming the command
        // that fixes it (NodeKeyStore.EnsureAsync's own message).
        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);

        if (node.PublicKey is null)
        {
            session.Events.Append(context.NodeId, NodeDecider.RegisterKey(node, key.PublicKeyLine, key.Fingerprint, now));
        }
        else if (node.PublicKey != key.PublicKeyLine)
        {
            throw new DomainConflictException(
                $"Node {context.NodeId}'s recorded public key no longer matches the key on disk at "
                + $"{NodeKeyStore.DirectoryFor(context.NodeId)} — something replaced the key files outside "
                + "h9k. Restore the original key files before joining again.");
        }

        string claimedFingerprint = claimedOwnerOverride.IsNotBlank() ? claimedOwnerOverride! : key.Fingerprint;
        bool establishingRoot = claimedFingerprint == key.Fingerprint;

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email! : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        bool retiredPreviousRoot = false;
        if (owner.RootFingerprint != claimedFingerprint)
        {
            if (owner.RootFingerprintVerified && owner.RootFingerprint == key.Fingerprint)
            {
                await RetireSelfRootAsync(
                    ledger, project.RepositoryPath, owner.RootFingerprint!, claimedFingerprint,
                    committer, signingKey, now, cancellationToken);
                retiredPreviousRoot = true;
            }

            session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, claimedFingerprint, establishingRoot, now));
        }

        if (establishingRoot)
        {
            await EnsureRootFileAsync(
                ledger, project.RepositoryPath, claimedFingerprint, key.PublicKeyLine, now,
                committer, signingKey, cancellationToken);
        }

        bool wroteNodeFile = await WriteNodeFileAsync(
            ledger, project.RepositoryPath, context.NodeId, key, claimedFingerprint,
            node.MachineName, node.OperatingSystem, node.KeyRegisteredAt ?? now,
            committer, signingKey, cancellationToken);

        if (node.ClaimedOwnerFingerprint != claimedFingerprint)
        {
            session.Events.Append(context.NodeId, NodeDecider.ClaimOwner(node, claimedFingerprint, now));
        }

        await session.SaveChangesAsync(cancellationToken);

        return new JoinOutcome(
            context.NodeId, key.Fingerprint, key.PrivateKeyPath, claimedFingerprint,
            establishingRoot, retiredPreviousRoot, wroteNodeFile);
    }

    internal static void Report(ProjectDetails project, JoinOutcome outcome)
    {
        AnsiConsole.MarkupLine(
            $"[green]Joined '{project.Name.EscapeMarkup()}'.[/] Node [dim]{outcome.NodeId}[/], "
            + $"key [dim]{outcome.KeyFingerprint}[/] at [dim]{outcome.PrivateKeyPath.EscapeMarkup()}[/].");
        AnsiConsole.MarkupLine(outcome.EstablishedRoot
            ? $"[dim]This is this owner's root — {outcome.ClaimedOwnerFingerprint} is the owner id everywhere in Hall9k now.[/]"
            : $"[dim]Claimed owner {outcome.ClaimedOwnerFingerprint}, unverified until the team half's vouch confirms it (not yet built).[/]");
        if (outcome.RetiredPreviousRoot)
        {
            AnsiConsole.MarkupLine("[yellow]This node's own previously self-created root was retired in favor of the claimed owner.[/]");
        }
    }

    private static async Task RetireSelfRootAsync(
        ILedger ledger, string repositoryPath, string retiredFingerprint, string realRootFingerprint,
        LedgerCommitter committer, LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{retiredFingerprint}";
        string path = $"owners/{retiredFingerprint}/retired.yaml";
        string content = BuildYaml(
            ("retired_at", now.ToString("o", CultureInfo.InvariantCulture)),
            ("real_root", realRootFingerprint));

        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (current.Content == content)
        {
            return;
        }

        await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, current.BlobId,
                $"Retire root {retiredFingerprint} in favor of {realRootFingerprint}", committer, signingKey),
            cancellationToken);
    }

    private static async Task EnsureRootFileAsync(
        ILedger ledger, string repositoryPath, string fingerprint, string publicKeyLine, DateTimeOffset createdAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{fingerprint}";
        string path = $"owners/{fingerprint}/root.yaml";

        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (current.Exists)
        {
            // Already established here — by this node, or found already present on a fresh fetch.
            return;
        }

        string content = BuildYaml(
            ("public_key", publicKeyLine),
            ("created_at", createdAt.ToString("o", CultureInfo.InvariantCulture)));

        await ledger.WriteAsync(
            new LedgerWriteRequest(repositoryPath, refName, path, content, ExpectedBlobId: null, $"Establish root {fingerprint}", committer, signingKey),
            cancellationToken);

        // A conflict here means another join won the race to establish the identical root between
        // the read above and this write — the root exists either way, which is what this call wanted.
    }

    private static async Task<bool> WriteNodeFileAsync(
        ILedger ledger, string repositoryPath, Guid nodeId, NodeSigningKey key, string claimedOwnerFingerprint,
        string machineName, string operatingSystem, DateTimeOffset joinedAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";

        string content = BuildYaml(
            ("node_id", nodeId.ToString()),
            ("public_key", key.PublicKeyLine),
            ("key_fingerprint", key.Fingerprint),
            ("owner_fingerprint", claimedOwnerFingerprint),
            ("machine_name", machineName),
            ("operating_system", operatingSystem),
            ("joined_at", joinedAt.ToString("o", CultureInfo.InvariantCulture)),
            ("invite_proof", null));

        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (current.Content == content)
        {
            return false;
        }

        LedgerWriteOutcome outcome = await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, current.BlobId,
                current.Exists ? "Update node facts" : "Join node", committer, signingKey),
            cancellationToken);
        return outcome.Verdict == LedgerWriteVerdict.Written;
    }

    /// <summary>
    /// A small, flat YAML document: every value double-quoted (a machine name or a comment on a
    /// public key line can carry spaces or colons a bare scalar would misparse), <c>null</c>
    /// rendered as the bare YAML null literal for the reserved invite-proof field. Every field
    /// written here is a plain string or timestamp, so this is deliberately not a general YAML
    /// writer — root.yaml, node.yaml, and retired.yaml are the only three documents A2a produces.
    /// </summary>
    private static string BuildYaml(params (string Key, string? Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string? value) in fields)
        {
            builder.Append(key).Append(": ").AppendLine(value is null ? "null" : QuoteYaml(value));
        }

        return builder.ToString();
    }

    private static string QuoteYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>
    /// Hall9k's own fingerprint shape (see <c>NodeKeyStore.Fingerprint</c>): exactly 64 lowercase
    /// hex characters. <c>\A</c>/<c>\z</c> rather than <c>^</c>/<c>$</c>: unanchored to line
    /// boundaries, <c>$</c> alone still matches immediately before a single trailing newline,
    /// which would let one slip through into a value this validation exists to keep out of a git
    /// ref name.
    /// </summary>
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex FingerprintPattern();
}
