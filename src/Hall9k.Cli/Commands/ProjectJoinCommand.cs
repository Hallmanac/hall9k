using System.ComponentModel;
using System.Globalization;
using System.Text;
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

public sealed class ProjectJoinCommand : Hall9kAsyncCommand<ProjectJoinCommand.Settings>
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
        JoinOutcome outcome;
        try
        {
            outcome = await RunAsync(session, project, settings.Owner, cancellationToken);
        }
        // A1's own git plumbing throws these as plain, undecorated exceptions rather than a
        // Domain*Exception — fine for h9k project add's own TryJoinAsync, which already wraps
        // every exception broadly and reports it as best-effort, but this standalone command has
        // no such wrapper, so either one previously escaped as an unhandled crash with a stack
        // trace instead of the "why" on stderr AGENTS.md's CLI standard requires (adversarial
        // review, cycle 1, low).
        catch (Exception exception) when (exception is LedgerPushRejectedException or InvalidOperationException)
        {
            throw new DomainValidationException(
                $"h9k project join could not finish against '{project.Name}'s own ledger: "
                + $"{exception.Message} Re-run h9k project join {project.Name} once that settles.");
        }

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
        bool WroteNodeFile,
        bool OwnerClaimChanged);

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
        if (claimedOwnerOverride.IsNotBlank() && !NodeKeyStore.IsFingerprint(claimedOwnerOverride))
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

        // No --owner keeps whatever root this owner already claims (including one claimed on a
        // different project's join) rather than falling back to this node's own key — falling back
        // unconditionally would flip a real owner's claim back to a self-created root on every
        // later plain join (independent pre-PR review, cycle 1, conformance and adversarial lenses,
        // both high: h9k project add's own join call always passes no --owner).
        string claimedFingerprint = claimedOwnerOverride.IsNotBlank()
            ? claimedOwnerOverride
            : owner.RootFingerprint ?? key.Fingerprint;
        // An explicit --owner always stays an unverified claim, even when it happens to name this
        // node's own fingerprint — establishing a root and marking it verified is reserved for the
        // no-argument path that lets a node's own key become the root in the first place.
        bool establishingRoot = claimedOwnerOverride.IsBlank() && claimedFingerprint == key.Fingerprint;

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        bool retiredPreviousRoot = false;
        if (owner.RootFingerprint != claimedFingerprint)
        {
            if (owner.RootFingerprintVerified && owner.RootFingerprint == key.Fingerprint)
            {
                // The owner's root claim is install-wide, but a self-created root.yaml may live in
                // every project's own ledger this node joined before the real owner was known — not
                // only the one project being joined right now, and not necessarily that one at all.
                // Retiring only "here" either leaves a live self-created root in every other project
                // (never retired), or writes a retired.yaml into a project that never had a root.yaml
                // to begin with (independent pre-PR review, cycle 1, conformance and adversarial
                // lenses, both medium).
                retiredPreviousRoot = await RetireSelfRootEverywhereAsync(
                    session, ledger, owner.Id, project, owner.RootFingerprint!, claimedFingerprint,
                    committer, signingKey, now, cancellationToken);
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

        // A node's own claim is install-wide (the Node stream), but node.yaml is only ever
        // rewritten in the project being joined right now, above — unlike root retirement, which
        // deliberately runs in every project this owner is registered to. A node.yaml already
        // written into some other, earlier-joined project under the old claim is left stale, so a
        // prior non-null claim that actually changes here is reported rather than left silent
        // (independent pre-PR review, cycle 1, conformance lens, low).
        bool ownerClaimChanged = node.ClaimedOwnerFingerprint is not null && node.ClaimedOwnerFingerprint != claimedFingerprint;
        if (node.ClaimedOwnerFingerprint != claimedFingerprint)
        {
            session.Events.Append(context.NodeId, NodeDecider.ClaimOwner(node, claimedFingerprint, now));
        }

        await session.SaveChangesAsync(cancellationToken);

        return new JoinOutcome(
            context.NodeId, key.Fingerprint, key.PrivateKeyPath, claimedFingerprint,
            establishingRoot, retiredPreviousRoot, wroteNodeFile, ownerClaimChanged);
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

        if (outcome.OwnerClaimChanged)
        {
            AnsiConsole.MarkupLine(
                "[yellow]This node's claimed owner changed. Any other project this node already joined still "
                + "has the old claim in its own node.yaml until h9k project join <project> runs there too.[/]");
        }
    }

    /// <summary>
    /// Retires <paramref name="retiredFingerprint"/>'s self-created root in every project this
    /// owner is registered to, but only where a <c>root.yaml</c> for it actually exists — a project
    /// this node never joined as itself never had one to retire. <paramref name="currentProject"/>
    /// is checked even when this session's own project query has not yet observed it (a same-session
    /// registration not yet visible to a fresh query).
    /// </summary>
    /// <remarks>
    /// Retiring in <paramref name="currentProject"/> is required — a failure there fails this join,
    /// the same as every other write it makes. Retiring in every <em>other</em> registered project
    /// is best-effort: an archived project is skipped outright, and an unreachable one (its remote
    /// deleted, or behind a VPN that is down) is reported and skipped rather than blocking the join
    /// the user actually asked for. A forgotten <c>--owner</c> is supposed to cost one rerun, not a
    /// detour through an unrelated project's own remote (independent pre-PR review, cycle 1,
    /// conformance and adversarial lenses, medium).
    /// </remarks>
    private static async Task<bool> RetireSelfRootEverywhereAsync(
        IDocumentSession session, ILedger ledger, Guid ownerId, ProjectDetails currentProject,
        string retiredFingerprint, string realRootFingerprint, LedgerCommitter committer,
        LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> ownedProjects = await session.Query<ProjectDetails>()
            .Where(p => p.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        string refName = $"refs/hall9k/ledger/owners/{retiredFingerprint}";
        string path = $"owners/{retiredFingerprint}/root.yaml";

        bool retiredAny = await RetireIfPresentAsync(
            ledger, currentProject, refName, path, retiredFingerprint, realRootFingerprint,
            committer, signingKey, now, cancellationToken);

        IEnumerable<ProjectDetails> otherProjects = ownedProjects
            .Where(p => p.Id != currentProject.Id && !p.IsArchived);
        foreach (ProjectDetails other in otherProjects)
        {
            try
            {
                retiredAny |= await RetireIfPresentAsync(
                    ledger, other, refName, path, retiredFingerprint, realRootFingerprint,
                    committer, signingKey, now, cancellationToken);
            }
            catch (Exception exception) when (exception is LedgerPushRejectedException or DomainConflictException or InvalidOperationException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not retire the previous root in '{other.Name.EscapeMarkup()}' "
                    + $"({exception.Message.EscapeMarkup()}) — skipped. Re-run h9k project join {other.Name.EscapeMarkup()} "
                    + "once that project is reachable again.[/]");
            }
        }

        return retiredAny;
    }

    private static async Task<bool> RetireIfPresentAsync(
        ILedger ledger, ProjectDetails project, string refName, string path,
        string retiredFingerprint, string realRootFingerprint, LedgerCommitter committer,
        LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        LedgerFile existing = await ledger.ReadAsync(project.RepositoryPath, refName, path, cancellationToken);
        if (!existing.Exists)
        {
            return false;
        }

        await RetireSelfRootAsync(
            ledger, project.RepositoryPath, retiredFingerprint, realRootFingerprint,
            committer, signingKey, now, cancellationToken);
        return true;
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

        // content depends only on this call's own arguments, so a Conflict here is always safe to
        // retry against a fresh tip, the same reasoning WriteNodeFileAsync's own retry applies —
        // the caller above saves the owner's new root claim on the strength of this retirement
        // actually landing, so a Conflict silently discarded here would let that claim commit
        // while retired.yaml either still held stale content or never recorded the retirement at
        // all (Copilot review, PR #366).
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    $"Retire root {retiredFingerprint} in favor of {realRootFingerprint}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this join after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project join once that settles.");
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

    /// <summary>How many times a conflicting ledger write retries against a fresh read before giving up.</summary>
    private const int MaxConflictRetries = 5;

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

        // content depends only on this call's own arguments, never on what is currently on disk,
        // so a Conflict — something else wrote node.yaml between the read and the write — is
        // always safe to retry against a fresh tip. The caller above saves the local identity
        // events on the strength of this file actually landing, so a lost Conflict silently
        // treated as "nothing to write" would let that claim commit while node.yaml still held
        // the old facts.
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
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
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return true;
            }
        }

        throw new DomainConflictException(
            $"node.yaml for node {nodeId} kept changing out from under this join after "
            + $"{MaxConflictRetries} attempts — something else is writing it at the same time. "
            + "Re-run h9k project join once that settles.");
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
}
