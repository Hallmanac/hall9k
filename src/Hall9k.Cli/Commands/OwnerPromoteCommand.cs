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
/// Promotes this node's own vouched key onto its owner's ranked root-key set (idea 6be68ee2, PR B
/// of the succession chain, stacked on 73d185b5): writes
/// <c>owners/&lt;root&gt;/rotations/&lt;n&gt;.yaml</c> — the promoting node's own vouched key and the
/// exact key it supersedes — into every non-archived project this owner is registered to, signed
/// with this node's own key, never the root's. Refused, per project, unless this node currently
/// holds a live successor record there (idea 6be68ee2: a listed candidate, not yet a root key, not
/// revoked) — the identical "one project's own lag never aborts what already landed elsewhere" shape
/// <see cref="NodeVouchCommand"/> and <see cref="NodeRevokeCommand"/> already use. A deliberate, loud
/// act: it asks for confirmation, printing which projects it will write to and the key each write
/// supersedes, and refuses outright in a non-interactive session with no <c>--yes</c> — a pty can
/// still fake a terminal, so the private key file's own 0600 permission, never this prompt, is the
/// real boundary (docs/concepts.md).
/// <para>
/// A re-run is idempotent: <see cref="RotateInProjectAsync"/> skips a project whose live root keys
/// already include this node's own and reports it as already rotated, so completing a partial
/// fan-out is simply running this command again. A project where another heir's rotation has
/// already landed loses the git-level compare-and-swap at <c>rotations/&lt;n&gt;.yaml</c> and is
/// refused there, by name, and never retried — a second, competing promotion is exactly the "two
/// valid promotions" case the succession design leaves to a human (HALL9K-P2P-DESIGN.md §6.5), never
/// something this command races again on its own.
/// </para>
/// </summary>
public sealed class OwnerPromoteCommand : Hall9kAsyncCommand<OwnerPromoteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--yes")]
        [Description(
            "Skip the confirmation prompt — required in a non-interactive session, since there is no "
            + "terminal to ask. The private key file's own 0600 permission is the real boundary either "
            + "way: a same-user process can already sign a rotation by hand with ssh-keygen and git.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), new ConsoleInteractiveConfirmation(), cancellationToken);
    }

    /// <summary>The whole promote flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// <see cref="NodeKeyStore"/>, and <see cref="IInteractiveConfirmation"/> so a test drives it
    /// against fakes (Brian's 2026-09-13 testing rule; the confirmation is seamed the same way
    /// <c>NodeVouchCommand</c> seams the ledger and the key store).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, IInteractiveConfirmation confirmation, CancellationToken cancellationToken)
    {
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

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.OwnerId == context.OwnerId && !project.IsArchived)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            throw new DomainValidationException("No project is registered to this owner yet — run h9k project join <project> first.");
        }

        // Dry run: every project this promotion still needs to reach, the exact key each write
        // would supersede, and why an ineligible project is still ineligible — computed and printed
        // before any push (criterion 1: "prints the projects it will write to, the key it
        // supersedes in each, and that an earlier key still outranks it and can undo it"), never
        // interleaved with the writes themselves, so the confirmation reflects one consistent read
        // rather than whatever each write happens to see moments later. One project's own lag or
        // refusal never aborts what another project would still let through — the identical
        // per-project independence NodeVouchCommand and NodeRevokeCommand already keep.
        List<(ProjectDetails Project, TrustChain Chain, string SupersededKey)> targets = [];
        List<string> alreadyRotatedPreview = [];
        List<string> ineligibleReasons = [];
        List<string> unreadableProjects = [];
        foreach (ProjectDetails project in projects)
        {
            TrustChain chain;
            try
            {
                chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                // Named and counted here, never a bare `continue`: a project this preview cannot
                // read is a partial fan-out the operator is not told about otherwise — the
                // confirmation would list only the reachable projects, the write would land there,
                // and the closing summary would report full success (independent pre-PR review,
                // cycle 1, both lenses, medium).
                unreadableProjects.Add(project.Name);
                AnsiConsole.MarkupLine(
                    $"[red]Could not read '{project.Name.EscapeMarkup()}'s own ledger "
                    + $"({exception.Message.EscapeMarkup()}) — skipped; re-run h9k owner promote once it is "
                    + "reachable again.[/]");
                continue;
            }

            if (chain.IsLiveRootKeyOfOwner(key.Fingerprint, root))
            {
                alreadyRotatedPreview.Add(project.Name);
                continue;
            }

            string? reason = IneligibilityReason(chain, root, context.NodeId, key.Fingerprint, project.RepositoryPath);
            if (reason is not null)
            {
                ineligibleReasons.Add(reason);
                AnsiConsole.MarkupLine(
                    $"[yellow]Not eligible to promote in '{project.Name.EscapeMarkup()}' yet — skipped ({reason.EscapeMarkup()}).[/]");
                continue;
            }

            TrustedOwner ownerChain = chain.OwnerChains[root];
            targets.Add((project, chain, ownerChain.RootKeys[^1].PublicKeyLine));
        }

        if (targets.Count == 0)
        {
            if (ineligibleReasons.Count > 0)
            {
                throw new DomainValidationException(ineligibleReasons[0]);
            }

            if (alreadyRotatedPreview.Count > 0)
            {
                throw new DomainValidationException(
                    $"This node ({key.Fingerprint}) already holds a live root key for owner {root} in every "
                    + "reachable project — nothing to promote.");
            }

            throw new DomainValidationException(
                $"Nothing to promote for node {context.NodeId} — no project's own copy of owner {root}'s chain "
                + "is currently reachable.");
        }

        if (!Confirm(targets, root, confirmation, settings.Yes))
        {
            await Console.Error.WriteLineAsync(
                "Refusing to promote without confirmation — nothing was touched. Re-run with --yes to skip "
                + "the prompt in a non-interactive session.");
            return ExitCodes.Error;
        }

        int rotatedInto = 0;
        List<string> alreadyRotated = [.. alreadyRotatedPreview];
        List<string> lostCompareAndSwap = [];
        // Seeded with the dry run's own unreadable projects, never dropped silently: this makes a
        // fetch failure at preview time surface exactly the way one at write time already does
        // (named, counted, and forced into the closing summary and its non-zero exit).
        List<string> failedProjects = [.. unreadableProjects];
        foreach ((ProjectDetails project, TrustChain _, string _) in targets)
        {
            try
            {
                RotationOutcome outcome = await RotateInProjectAsync(
                    ledger, chainReader, project.RepositoryPath, root, context.NodeId, key, now, owner, cancellationToken);
                switch (outcome)
                {
                    case RotationOutcome.Rotated:
                        rotatedInto++;
                        break;
                    case RotationOutcome.AlreadyRotated:
                        alreadyRotated.Add(project.Name);
                        break;
                    case RotationOutcome.LostCompareAndSwap:
                        lostCompareAndSwap.Add(project.Name);
                        AnsiConsole.MarkupLine(
                            $"[red]Another heir's rotation already landed in '{project.Name.EscapeMarkup()}'[/] — "
                            + "refusing rather than racing a second promotion there. See "
                            + "HALL9K-P2P-DESIGN.md §6.5; h9k node revoke <heir-node-id> from a node holding an "
                            + "earlier root key undoes a hijacked one.");
                        break;
                }
            }
            catch (DomainValidationException exception)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not promote in '{project.Name.EscapeMarkup()}' — skipped ({exception.Message.EscapeMarkup()}).[/]");
            }
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException or DomainConflictException)
            {
                failedProjects.Add(project.Name);
                AnsiConsole.MarkupLine(
                    $"[red]Failed to promote in '{project.Name.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}) — "
                    + "re-run h9k owner promote once fixed.[/]");
            }
        }

        if (rotatedInto == 0 && alreadyRotated.Count == 0)
        {
            throw new DomainValidationException(
                $"Nothing was promoted for node {context.NodeId} in any project — see the messages above for "
                + "why (a lost compare-and-swap is never retried automatically; every other failure can be "
                + "fixed and re-run).");
        }

        AnsiConsole.MarkupLine(
            $"[green]Promoted[/] node [dim]{context.NodeId}[/] [dim](key fingerprint {key.Fingerprint})[/] onto "
            + $"owner [dim]{root}[/]'s own ranked root-key set, in {rotatedInto} project(s)"
            + (alreadyRotated.Count > 0 ? $", already rotated in {alreadyRotated.Count}" : string.Empty)
            + (lostCompareAndSwap.Count > 0 ? $", refused in {lostCompareAndSwap.Count} by another heir" : string.Empty)
            + (failedProjects.Count > 0 ? $", failed in {failedProjects.Count}" : string.Empty)
            + ". Every node reading each project's own ledger names this rotation on its next sweep.");

        if (failedProjects.Count > 0)
        {
            throw new DomainValidationException(
                $"Promoted node {context.NodeId} in {rotatedInto} project(s), but failed in "
                + $"{failedProjects.Count}: {string.Join(", ", failedProjects)} — re-run h9k owner promote once "
                + "fixed; the retry is idempotent and only writes to projects still missing the rotation.");
        }

        return ExitCodes.Ok;
    }

    private enum RotationOutcome
    {
        Rotated,
        AlreadyRotated,
        LostCompareAndSwap,
    }

    /// <summary>
    /// Why <paramref name="nodeId"/> cannot promote itself in this one project's own copy of
    /// <paramref name="root"/>'s chain right now — most specific first: revoked (never eligible
    /// again until re-vouched), not vouched at all, or vouched with no successor record yet. Null
    /// when none of those apply (the caller has already ruled out "already a live root key" before
    /// calling this).
    /// </summary>
    private static string? IneligibilityReason(TrustChain chain, string root, Guid nodeId, string myFingerprint, string repositoryPath)
    {
        string nodeIdText = nodeId.ToString();
        if (!chain.OwnerChains.TryGetValue(root, out TrustedOwner? ownerChain))
        {
            return $"Owner {root} has no chain in '{repositoryPath}' yet.";
        }

        if (ownerChain.RevokedNodeIds.Contains(nodeIdText))
        {
            return $"This node ({myFingerprint}) is revoked from owner {root}'s own fleet in "
                + $"'{repositoryPath}' — a revoked node cannot promote itself. Ask a live root key to vouch "
                + $"it again first: h9k node vouch {nodeId}.";
        }

        if (!ownerChain.Nodes.Any(node => node.NodeId == nodeIdText && node.Fingerprint == myFingerprint))
        {
            return $"This node ({myFingerprint}) is not currently vouched into owner {root}'s own fleet in "
                + $"'{repositoryPath}' — nothing to promote until an enrolled node vouches it: h9k node vouch {nodeId}.";
        }

        if (!ownerChain.SuccessorNodeIds.Contains(nodeIdText))
        {
            return $"This node ({myFingerprint}) has no successor record yet for owner {root} in "
                + $"'{repositoryPath}' — only a node a live root key vouched receives one automatically. Ask "
                + $"a live root key to re-run h9k node vouch {nodeId} if it is missing.";
        }

        return null;
    }

    private static bool Confirm(
        IReadOnlyList<(ProjectDetails Project, TrustChain Chain, string SupersededKey)> targets, string root,
        IInteractiveConfirmation confirmation, bool yes)
    {
        if (yes)
        {
            return true;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]Promoting this node onto owner {root}'s own ranked root-key set[/] writes a rotation, "
            + "signed by this node's own key, into:");
        foreach ((ProjectDetails project, TrustChain chain, string supersededKey) in targets)
        {
            AnsiConsole.MarkupLine(
                $"  [dim]{project.Name.EscapeMarkup()}[/] — supersedes {ShortKey(supersededKey)}"
                + $"{RootNodeDescription.Of(chain, root)}");
        }

        AnsiConsole.MarkupLine(
            "[dim]The key it supersedes still outranks this one and can undo it "
            + $"(owners/{root}/revoked-successors/<node-id>.yaml, signed by that earlier key). This command has "
            + "no undo of its own, and a peer's next sweep names this rotation the moment it observes it.[/]");

        if (!confirmation.IsInteractive)
        {
            AnsiConsole.MarkupLine("[red]Refusing[/]: this session cannot prompt for confirmation. Pass --yes to proceed anyway.");
            return false;
        }

        return confirmation.Confirm("Promote this node now?", defaultValue: false);
    }

    /// <summary>A public key line's own short form for a confirmation prompt: the same fingerprint
    /// hashing <see cref="NodeKeyStore.Fingerprint"/> gives every other trust surface, shortened.</summary>
    private static string ShortKey(string publicKeyLine)
    {
        try
        {
            string fingerprint = NodeKeyStore.Fingerprint(publicKeyLine);
            return fingerprint[..Math.Min(12, fingerprint.Length)];
        }
        catch (DomainValidationException)
        {
            return "an unreadable key";
        }
    }

    /// <summary>
    /// Refuses before any push when this project's own copy no longer agrees this node is eligible
    /// (defense in depth — the dry-run pass already filtered on the identical facts moments
    /// earlier), returns <see cref="RotationOutcome.AlreadyRotated"/> without writing when this
    /// project's live root keys already include this node's own key (idempotent re-run), attempts
    /// exactly one compare-and-swap write at the next open <c>rotations/&lt;n&gt;.yaml</c> slot, and
    /// returns <see cref="RotationOutcome.LostCompareAndSwap"/> — never retried at a later slot — when
    /// another heir's rotation already occupies it.
    /// </summary>
    private static async Task<RotationOutcome> RotateInProjectAsync(
        ILedger ledger, ILedgerChainReader chainReader, string repositoryPath, string root, Guid nodeId,
        NodeSigningKey key, DateTimeOffset now, OwnerAggregate owner, CancellationToken cancellationToken)
    {
        TrustChain chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
        if (chain.IsLiveRootKeyOfOwner(key.Fingerprint, root))
        {
            return RotationOutcome.AlreadyRotated;
        }

        if (IneligibilityReason(chain, root, nodeId, key.Fingerprint, repositoryPath) is { } reason)
        {
            throw new DomainValidationException(reason);
        }

        TrustedOwner ownerChain = chain.OwnerChains[root];
        string supersededKey = ownerChain.RootKeys[^1].PublicKeyLine;
        string refName = $"refs/hall9k/ledger/owners/{root}";
        string rotationsPrefix = $"owners/{root}/rotations/";

        // The sequence number this rotation targets is the next slot past every rotation file
        // CURRENTLY IN THE LEDGER, validated or not — never this project's own count of validated
        // root keys: a revoked rotation's own file is never removed (only its content stops
        // counting once a higher-ranked key voids it), so counting validated keys alone reused
        // that exact slot forever the moment anything ever voided or superseded whatever first
        // landed there, permanently losing every later promotion's own compare-and-swap in that
        // project (independent pre-PR review, cycle 1, both lenses, high). Scanning the ledger's
        // own current file listing instead guarantees a genuinely open path regardless of what any
        // of those files validate to. Two nodes racing a promotion from the identical starting
        // ledger state see the identical listing and so target the identical next slot — exactly
        // the property the compare-and-swap below still needs: the "two valid promotions" race
        // (HALL9K-P2P-DESIGN.md §6.5) resolves at the git level, first push wins, and the loser is
        // refused here rather than silently landing a second, competing rotation.
        IReadOnlyList<LedgerEntry> existingRotations =
            await ledger.ReadAllAsync(repositoryPath, refName, rotationsPrefix, cancellationToken);
        int sequence = 1;
        foreach (LedgerEntry entry in existingRotations)
        {
            string suffix = entry.Path[rotationsPrefix.Length..];
            if (suffix.EndsWith(".yaml", StringComparison.Ordinal)
                && int.TryParse(suffix[..^".yaml".Length], out int existingSequence)
                && existingSequence >= sequence)
            {
                sequence = existingSequence + 1;
            }
        }

        string path = $"{rotationsPrefix}{sequence}.yaml";
        string content = BuildYaml(
            ("node_id", nodeId.ToString()),
            ("public_key", key.PublicKeyLine),
            ("supersedes_public_key", supersededKey),
            ("issued_at", now.ToString("o", CultureInfo.InvariantCulture)));

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{nodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        // Exactly one attempt, never retried: two nodes computing the identical next sequence
        // number from the identical starting state is the "two valid promotions" race
        // (HALL9K-P2P-DESIGN.md §6.5), and racing again at sequence+1 would let this node layer a
        // second, competing rotation on top of whichever heir actually won — a decision the
        // succession design leaves to a human, never to an automatic retry loop.
        LedgerWriteOutcome outcome = await ledger.WriteAsync(
            new LedgerWriteRequest(repositoryPath, refName, path, content, ExpectedBlobId: null, $"Rotate to node {nodeId}", committer, signingKey),
            cancellationToken);
        return outcome.Verdict == LedgerWriteVerdict.Written ? RotationOutcome.Rotated : RotationOutcome.LostCompareAndSwap;
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
