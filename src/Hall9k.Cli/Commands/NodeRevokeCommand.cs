using System.ComponentModel;
using System.Globalization;
using System.Linq;
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

        // Refused before any push, across every project (idea 6be68ee2, trust-ledger finding 2): a
        // compromised fleet node must never revoke its own peers, so this node's own key must itself
        // be a LIVE ROOT KEY of this owner — never merely enrolled, which NodeVouchCommand's own
        // identical-looking gate still correctly accepts for a vouch. Root-key status is an
        // owner-wide fact the succession fan-out (73d185b5) usually keeps in step across every
        // project this owner is registered to, but one project's own ledger copy can still lag or be
        // briefly unreachable — checked against EVERY project rather than only the first, so this
        // early gate only needs ONE project to confirm the key is currently live, and a single stale
        // or unreachable project's own copy never aborts a revocation that would have succeeded
        // everywhere else (independent pre-PR review, cycle 1, both lenses, medium: reading only
        // projects[0], an unordered query's own first row, let one bad project block the whole
        // command, or fail it outright on an unreachable ledger). When every project's own read
        // fails outright, this gate steps aside rather than reporting a guess — RevokeInProjectAsync's
        // own identical check, per project, is what actually enforces this on every push regardless,
        // and its own per-project try/catch below is what then reports the real failure.
        bool anyProjectReadable = false;
        bool confirmedLiveRootKey = false;
        TrustChain? lastReadableChain = null;
        foreach (ProjectDetails earlyProject in projects)
        {
            TrustChain earlyChain;
            try
            {
                earlyChain = await chainReader.ComputeAsync(earlyProject.RepositoryPath, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            anyProjectReadable = true;
            lastReadableChain = earlyChain;
            if (earlyChain.IsLiveRootKeyOfOwner(key.Fingerprint, root))
            {
                confirmedLiveRootKey = true;
                break;
            }
        }

        if (anyProjectReadable && !confirmedLiveRootKey)
        {
            string rootNodeDescription = lastReadableChain is { } chainForMessage ? RootNodeDescription.Of(chainForMessage, root) : string.Empty;
            throw new DomainValidationException(
                $"This node ({key.Fingerprint}) does not currently hold a live root key for owner {root} — "
                + "only the root itself may revoke a node from its own fleet (idea 6be68ee2, trust-ledger "
                + $"finding 2: a vouched node key can no longer revoke its peers). Re-run h9k node revoke "
                + $"{targetNodeId} from a node holding a root key for {root}{rootNodeDescription}.");
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
                // Never a hardcoded "not enrolled there" suffix: RevokeInProjectAsync only throws
                // this for genuine non-enrollment today, but exception.Message already states the
                // real reason, so nothing here needs to restate — and this stays correct even if a
                // second cause is ever added, the identical sibling gap independent pre-PR review
                // (cycle 1, conformance lens, low) found in NodeVouchCommand's own identical catch.
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not revoke in '{project.Name.EscapeMarkup()}' — skipped ({exception.Message.EscapeMarkup()}).[/]");
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
        if (!chain.IsLiveRootKeyOfOwner(myFingerprint, root))
        {
            throw new DomainValidationException(
                $"This node ({myFingerprint}) does not currently hold a live root key for owner {root} in "
                + $"'{repositoryPath}' — only the root itself may revoke a node from its own fleet (idea "
                + "6be68ee2, trust-ledger finding 2). Re-run h9k node revoke from a node holding a root "
                + $"key for {root}{RootNodeDescription.Of(chain, root)}.");
        }

        // Whether the target currently holds LIVE ROOT KEY status via a validated rotation — never
        // merely an ordinary fleet node. The order-aware carve-out ComputeSuccessionAsync applies to
        // a rotation record (independent pre-PR review, cycle 4, adversarial lens, medium) means an
        // ordinary owners/<root>/revoked/<node-id>.yaml write like the one below no longer strips a
        // rotated-in node's own root-key status by itself, on purpose — that is what the paired
        // revoked-successor write just below actually does, checked as "signed by a key ranked
        // above the target," which this node's own confirmed-live-root-key gate above always
        // satisfies. So when the target holds that status, this method's own success can no longer
        // rest on the ordinary revoke landing alone: the paired write failing must fail the whole
        // project rather than merely warn, or the target silently keeps a live root key — able to
        // revoke its own peers and rewrite membership — with nothing in `h9k status` ever saying so.
        // Whether this node's own already-confirmed-live root key actually outranks the target's
        // own root-key entry, when the target holds one. GitLedgerChainReader's own succession pass
        // (the "chain.Take(chainIndex)" eligibility check for a revoked-successor record) never
        // accepts that record merely because the signer is SOME live root key — it must be ranked
        // strictly above the one it targets. SuccessionLedgerWriter writes the record unconditionally
        // regardless (its own class doc: "write it, let the reader verify it"), so an outranked
        // signer's write lands at the git level with no exception thrown, and the record is then
        // silently refused on the next read — the target keeps live root-key status forever unless
        // this is caught before it can report ordinary success (independent pre-PR review, cycle 5,
        // adversarial lens, medium).
        bool signerOutranksTarget = SignerOutranksTarget(chain, root, targetNodeId, myFingerprint, out bool targetHoldsRootKeyStatus);

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
                // Never even attempted when it is already known to be doomed: this node is a live
                // root key (the gate above), but not every live root key outranks the target's own
                // — writing anyway would land at the git level and then be silently refused on the
                // next read, reporting ordinary success while the target keeps live root-key status.
                if (targetHoldsRootKeyStatus && !signerOutranksTarget)
                {
                    throw new DomainConflictException(
                        $"Revoked node {targetNodeId}'s ordinary fleet record in '{repositoryPath}', but this "
                        + $"node ({myFingerprint}) is not ranked above {targetNodeId}'s own root key, so its "
                        + "revoked-successor record would be refused on read and this node currently holds a "
                        + $"live root key, so it still would. Re-run h9k node revoke {targetNodeId} from a node "
                        + "whose own root key outranks it.");
                }

                // Unconditional now (idea 6be68ee2, journal finding 5): the gate above already
                // requires the revoking key to be a live root key before any push, so every
                // revocation that lands here also revokes this node id's own successor candidacy or
                // rotation, if it has one — a revoked-successor record only counts on read when it
                // is signed by a root key ranked above the successor it targets, which this write
                // always is. Best-effort ONLY when the target never held root-key status in the
                // first place, the same reasoning as the vouch side: the revocation itself already
                // landed, and there is no root-key trust left dangling behind a swallowed exception.
                try
                {
                    await SuccessionLedgerWriter.WriteRevokedSuccessorAsync(
                        ledger, repositoryPath, root, targetNodeId, now, committer, signingKey, cancellationToken);
                }
                catch (Exception exception)
                    when (exception is LedgerPushRejectedException or InvalidOperationException or DomainConflictException)
                {
                    if (targetHoldsRootKeyStatus)
                    {
                        throw new DomainConflictException(
                            $"Revoked node {targetNodeId}'s ordinary fleet record in '{repositoryPath}', but "
                            + $"could not also write its revoked-successor record ({exception.Message}) — this "
                            + "node currently holds a live root key, so it still does until that record lands. "
                            + $"Re-run h9k node revoke {targetNodeId} once fixed.");
                    }

                    AnsiConsole.MarkupLine(
                        $"[yellow]Revoked node {targetNodeId}, but could not also write its revoked-successor record "
                        + $"in '{repositoryPath.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}).[/]");
                }

                return true;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this revocation after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k node revoke once that settles.");
    }

    /// <summary>Whether <paramref name="signerFingerprint"/> is ranked above the target node's own
    /// root-key entry, mirroring <c>GitLedgerChainReader</c>'s own <c>chain.Take(chainIndex)</c>
    /// eligibility check for a revoked-successor record — never merely whether the signer is SOME
    /// live root key. <paramref name="targetHoldsRootKeyStatus"/> is true only when the target
    /// currently holds live root-key status at all; when it does not, rank is irrelevant and this
    /// always returns true so the caller's own gate never fires.</summary>
    private static bool SignerOutranksTarget(
        TrustChain chain, string root, Guid targetNodeId, string signerFingerprint, out bool targetHoldsRootKeyStatus)
    {
        targetHoldsRootKeyStatus = false;
        if (!chain.OwnerChains.TryGetValue(root, out TrustedOwner? ownerChain))
        {
            return true;
        }

        TrustedNode? targetNode = ownerChain.Nodes.FirstOrDefault(node => node.NodeId == targetNodeId.ToString());
        if (targetNode is null || !ownerChain.IsLiveRootKey(targetNode.Fingerprint))
        {
            return true;
        }

        targetHoldsRootKeyStatus = true;
        int targetRank = RankIndexOf(ownerChain.RootKeys, targetNode.Fingerprint);
        int signerRank = RankIndexOf(ownerChain.RootKeys, signerFingerprint);
        return signerRank >= 0 && targetRank >= 0 && signerRank < targetRank;
    }

    /// <summary>This root's own key rank: index 0 is K0, the highest rank, each later index a
    /// rotation that landed after it — the identical order <c>TrustedOwner.RootKeys</c> itself
    /// documents. -1 when <paramref name="fingerprint"/> is not one of this root's live keys.</summary>
    private static int RankIndexOf(IReadOnlyList<LiveRootKey> rootKeys, string fingerprint)
    {
        for (int index = 0; index < rootKeys.Count; index++)
        {
            if (rootKeys[index].Fingerprint == fingerprint)
            {
                return index;
            }
        }

        return -1;
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
