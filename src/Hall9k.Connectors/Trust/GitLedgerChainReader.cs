using System.Globalization;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// The real <see cref="ILedgerChainReader"/>: walks a project's own bare repository by git
/// plumbing alone — the same reasoning <see cref="Hall9k.Connectors.Messaging.GitLedgerMessageTransport"/>
/// reads a messages ref directly rather than through <c>ILedger</c> (a single-path read, never an
/// enumeration or a signature check, is all that component's own contract offers).
/// <para>
/// Discovers every owner root this project's ledger has ever seen
/// (<c>refs/hall9k/ledger/owners/*</c> by one <c>ls-remote</c>), computes each root's own chain —
/// its self-certified key plus every node currently vouched into it, latest of vouch or
/// revocation winning in ref commit order — independent of project membership, then replays
/// <c>refs/hall9k/ledger/members</c> against those chains: the very first commit ever touching a
/// members file is genesis (self-written, unconditional), every later one needs a signer that is,
/// as of this read, an Owner-role member's own root key or a node currently in that root's own
/// chain — the chain's live state, recomputed fresh on every call, never a snapshot pinned to any
/// commit's own claimed committer date, which is a field its writer freely chooses and so is never
/// a trustworthy anchor for anything (independent pre-PR review, cycle 1; ruled by the window,
/// 2026-09-13: one rule, checked against the chain as it stands right now, no committer-timestamp
/// pinning and no first-write exception). Consequence, accepted rather than deferred: a revocation
/// retroactively voids every membership write the revoked node ever signed, and a later re-vouch of
/// that same node restores them on the very next read — the identical latest-of-vouch-or-revocation
/// rule the owner chain itself already applies, extended to the membership writes that chain in
/// turn authorizes. A write whose signer cannot be verified this way is silently ignored — never a
/// thrown exception — so a stranger's self-consistent root and node files simply never enter
/// <see cref="TrustChain.OwnerChains"/>'s membership-restricted view; it is instead recorded in
/// <see cref="TrustChain.UnverifiedWrites"/>, so a caller can name the writer rather than let it
/// vanish without a trace.
/// </para>
/// <para>
/// Every commit walk here replays <c>--topo-order --first-parent</c>: a commit reachable only
/// through a merge's second parent is never replayed at all, so a forged commit cannot backdate
/// its own committer timestamp to slot itself earlier in ref order by riding in on a merge
/// (independent pre-PR review, cycle 1, adversarial lens, medium) — replay order is the actual
/// mainline commit graph, not a timestamp any pusher freely controls.
/// </para>
/// <para>
/// A network or credential failure fetching a ref, or listing the owners prefix at all, is thrown
/// rather than folded into an empty or partial chain — the identical reasoning
/// <c>GitLedgerMessageTransport.ProbeCoreAsync</c> already applies, so an unreachable ledger never
/// looks like "nothing here" to a caller deciding what to report (independent pre-PR review,
/// cycle 1, adversarial lens, medium).
/// </para>
/// <para>
/// Never covered by <c>GitLedgerMessageTransport</c>'s own tests or any test above A1: this class'
/// own tests (<c>GitLedgerChainReaderTests</c>) are the second place, besides <c>GitLedgerTests</c>,
/// a throwaway bare repository is used at all (Brian's 2026-09-13 testing rule).
/// </para>
/// </summary>
public sealed class GitLedgerChainReader(ProcessRunner? runner = null) : ILedgerChainReader
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    private const string OwnersRefPrefix = "refs/hall9k/ledger/owners/";
    private const string NodesRefPrefix = "refs/hall9k/ledger/nodes/";
    private const string MembersRefName = "refs/hall9k/ledger/members";

    /// <summary>The only principal <see cref="IsSignedByAsync"/> ever writes into a temporary
    /// <c>allowed_signers</c> file — a fixed literal, never the commit's own committer email. That
    /// field was previously the (attacker-controlled) committer email of the very commit under
    /// verification: a crafted email containing a space let a malicious pusher smuggle their own
    /// key into the allowed-signers line's key-type/key-data fields, displacing the real candidate
    /// key into a trailing, ignored comment, so <c>git verify-commit</c> verified a forged commit
    /// against the attacker's own key instead — the identical injection
    /// <c>GitLedgerMessageTransport.AllowedSignersPrincipal</c>'s own doc comment already fixed
    /// there (independent pre-PR review, cycle 1, conformance and adversarial lenses, both high).
    /// <c>git verify-commit</c> never requires this principal to match the commit's own committer
    /// identity, so a fixed principal costs nothing: every candidate key already came from this
    /// project's own ledger, never from anything a commit's own author controls.</summary>
    private const string AllowedSignersPrincipal = "hall9k-chain-writer";

    public async Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = await DiscoverOwnerRootsAsync(repositoryPath, cancellationToken);

        Dictionary<string, TrustedOwner> ownerChains = [];
        List<UnverifiedLedgerWrite> unverified = [];
        foreach (string root in roots)
        {
            (TrustedOwner? chain, IReadOnlyList<UnverifiedLedgerWrite> rootUnverified) =
                await ComputeOwnerChainAsync(repositoryPath, root, cancellationToken);
            unverified.AddRange(rootUnverified);
            if (chain is not null)
            {
                ownerChains[root] = chain;
            }
        }

        (IReadOnlyList<UnverifiedLedgerWrite> nodeUnverified, IReadOnlyDictionary<string, NodeGitHubDeclaration> declarations,
            IReadOnlyDictionary<string, NodeDisplayNameDeclaration> displayNames) =
            await AttachRootNodeIdsAsync(repositoryPath, ownerChains, cancellationToken);
        unverified.AddRange(nodeUnverified);

        (IReadOnlyList<ProjectMember> members, IReadOnlyList<UnverifiedLedgerWrite> memberUnverified,
            string? genesisRootFingerprint, string? projectKey) =
            await ComputeMembersAsync(repositoryPath, ownerChains, cancellationToken);
        unverified.AddRange(memberUnverified);

        return new TrustChain(
            ownerChains, members, unverified, genesisRootFingerprint, projectKey, declarations, displayNames);
    }

    /// <summary>Every <c>refs/hall9k/ledger/owners/&lt;fingerprint&gt;</c> ref origin currently
    /// holds — the owners prefix's own suffixes, one per root fingerprint.</summary>
    private Task<IReadOnlyList<string>> DiscoverOwnerRootsAsync(string repositoryPath, CancellationToken cancellationToken) =>
        DiscoverRefSuffixesAsync(repositoryPath, OwnersRefPrefix, cancellationToken);

    /// <summary>Every <c>refs/hall9k/ledger/nodes/&lt;node-id&gt;</c> ref origin currently holds —
    /// every node this project's ledger has ever seen announce itself, one per node id, regardless
    /// of whether that node has ever been vouched into anyone's own chain.</summary>
    private Task<IReadOnlyList<string>> DiscoverNodeIdsAsync(string repositoryPath, CancellationToken cancellationToken) =>
        DiscoverRefSuffixesAsync(repositoryPath, NodesRefPrefix, cancellationToken);

    /// <summary>Every ref under <paramref name="prefix"/> origin currently holds, from one
    /// <c>ls-remote</c> against the whole prefix, as the suffix past that prefix — mirrors
    /// <c>GitLedgerMessageTransport.ProbeCoreAsync</c>'s own parsing exactly, including its own
    /// choice to throw on a genuine failure rather than read one as "nothing under this prefix"
    /// (independent pre-PR review, cycle 1, adversarial lens, medium). Shared by
    /// <see cref="DiscoverOwnerRootsAsync"/> and <see cref="DiscoverNodeIdsAsync"/> — identical
    /// shape, different prefix.</summary>
    private async Task<IReadOnlyList<string>> DiscoverRefSuffixesAsync(
        string repositoryPath, string prefix, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["ls-remote", "origin", $"{prefix}*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-remote against {repositoryPath} for the {prefix} prefix failed "
                + $"(exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        HashSet<string> suffixes = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string refName = parts[1].Trim();
            if (refName.StartsWith(prefix, StringComparison.Ordinal))
            {
                suffixes.Add(refName[prefix.Length..]);
            }
        }

        // Unioned with whatever this node's own local refs/hall9k-verified/ tree already holds under
        // this prefix, so a ref origin has since deleted (a removed owners/<fp> or nodes/<id>) is
        // still discovered as the missing-remote-ref refusal it is, rather than silently dropped from
        // discovery the moment ls-remote no longer lists it (idea 6be68ee2, trust finding 8).
        IReadOnlyList<string> verifiedSuffixes =
            await LedgerAppendOnlyRefFetcher.DiscoverLocallyVerifiedSuffixesAsync(runner, repositoryPath, prefix, cancellationToken);
        suffixes.UnionWith(verifiedSuffixes);

        return [.. suffixes];
    }

    /// <summary>
    /// Finds each root's own node id — the node whose own key established that root
    /// (<c>h9k project join</c>'s no-<c>--owner</c> path writes <c>root.yaml</c> under that exact
    /// node's own key) — so a root with no <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> vouch
    /// entry of its own (a root never vouches itself) still resolves as part of its own fleet
    /// (<see cref="TrustedOwner.FleetNodeIds"/>). Scans every node this project's ledger has ever
    /// seen announce itself (<c>refs/hall9k/ledger/nodes/*</c>), reading each one's own
    /// self-announced <c>node.yaml</c>: a node whose declared public key fingerprints back to a
    /// root already in <paramref name="ownerChains"/> is trusted as that root's own node only when
    /// the commit that currently produces that content is signed by that identical root key — the
    /// same self-consistency <c>ComputeOwnerChainAsync</c>'s own root.yaml check already applies,
    /// since a node cannot forge a signature over a key it does not hold. A candidate that
    /// fingerprints back to a trusted root but fails that signature check is recorded into the
    /// returned list rather than silently skipped (independent pre-PR review, cycle 1, adversarial
    /// lens, medium) — the identical "name the writer" contract this class' own doc states for
    /// every other verification surface. Skipped entirely when nothing self-certified above ever
    /// produced an owner chain to attach a node id to.
    /// <para>
    /// A candidate's own declared <c>owner_fingerprint</c> field must still equal the very root its
    /// key fingerprints to, not merely have equalled it once: a node that self-created a root and
    /// later re-runs <c>h9k project join --owner</c> against its real owner keeps the same key (so
    /// it still fingerprints back to the root it originally established) while
    /// <c>RetireSelfRootEverywhereAsync</c> retires that self-created root by adding a
    /// <c>retired.yaml</c> beside its <c>root.yaml</c> — never removing <c>root.yaml</c> itself, so
    /// the retired root still self-certifies above — and the rerun's own <c>NodeFileWriter.WriteAsync</c>
    /// rewrites that node's <c>owner_fingerprint</c> to the real owner. Without this check, the
    /// retired root's chain would re-attach that node as its own root node forever, and
    /// <c>EventCatchUpCoordinator.ResolveMemberRole</c> would resolve the node's role against the
    /// retired, non-member root instead of its real owner whenever the two chains happen to iterate
    /// in that order (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </para>
    /// <para>
    /// Every node ref this scan could possibly need is fetched once, by a single wildcard refspec,
    /// rather than one <c>git fetch</c> per node id: <see cref="DiscoverNodeIdsAsync"/> already
    /// names exactly which node ids origin currently holds, so a wildcard fetch of that same prefix
    /// costs one network round trip regardless of fleet size instead of one per node
    /// (independent pre-PR review, cycle 1, adversarial lens, medium — this scan's per-call cost
    /// used to scale with the project's total node count, not its root count, and
    /// <c>Hall9k.Daemon.Messaging.MessageSweepEngine</c> calls <see cref="ComputeAsync"/>
    /// once per project on every sweep tick). The loop itself walks every discovered node id rather
    /// than stopping once every owner chain already has a <see cref="TrustedOwner.RootNodeId"/>:
    /// <c>ls-remote</c> returns refs in refname order, not discovery order, so a forged
    /// <c>node.yaml</c> claiming an already-resolved root's own key can sort after the genuine one,
    /// and stopping early would let that forgery go unrecorded depending on nothing more meaningful
    /// than node-id sort order (independent pre-PR review, cycle 3, both lenses, medium).
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<UnverifiedLedgerWrite> Unverified, IReadOnlyDictionary<string, NodeGitHubDeclaration> Declarations,
        IReadOnlyDictionary<string, NodeDisplayNameDeclaration> DisplayNames)>
        AttachRootNodeIdsAsync(
            string repositoryPath, Dictionary<string, TrustedOwner> ownerChains, CancellationToken cancellationToken)
    {
        if (ownerChains.Count == 0)
        {
            return ([], new Dictionary<string, NodeGitHubDeclaration>(), new Dictionary<string, NodeDisplayNameDeclaration>());
        }

        IReadOnlyList<string> nodeIds = await DiscoverNodeIdsAsync(repositoryPath, cancellationToken);
        if (nodeIds.Count == 0)
        {
            return ([], new Dictionary<string, NodeGitHubDeclaration>(), new Dictionary<string, NodeDisplayNameDeclaration>());
        }

        IReadOnlyDictionary<string, LedgerAppendOnlyFetchResult> fetchResults =
            await LedgerAppendOnlyRefFetcher.FetchPrefixAsync(runner, repositoryPath, NodesRefPrefix, nodeIds, cancellationToken);

        List<UnverifiedLedgerWrite> unverified = [];
        Dictionary<string, NodeGitHubDeclaration> declarations = [];
        Dictionary<string, NodeDisplayNameDeclaration> displayNames = [];
        foreach (string nodeId in nodeIds)
        {
            string refName = $"{NodesRefPrefix}{nodeId}";
            LedgerAppendOnlyFetchResult fetchResult = fetchResults[nodeId];
            if (fetchResult.WasRefused)
            {
                unverified.Add(new UnverifiedLedgerWrite("node", nodeId, nodeId, fetchResult.RefusalReason!));
            }

            string? tip = fetchResult.Tip;
            if (tip is null)
            {
                continue;
            }

            string path = $"nodes/{nodeId}/node.yaml";
            string? content = await ReadAtCommitAsync(repositoryPath, tip, path, cancellationToken);
            string? publicKeyLine = content is null ? null : ExtractQuotedYamlValue(content, "public_key");
            string? ownerFingerprintLine = content is null ? null : ExtractQuotedYamlValue(content, "owner_fingerprint");
            if (publicKeyLine is null || !TryFingerprint(publicKeyLine, out string fingerprint))
            {
                // No key, or an unparseable one: nothing about this file can be attributed to anyone.
                continue;
            }

            bool namesTrustedRoot = ownerChains.ContainsKey(fingerprint);
            IReadOnlyList<string>? commits = null;

            // The GitHub account this node declares for itself counts only when the commit that
            // currently produces the file is signed by the file's own public key: anyone with push
            // could otherwise rewrite another node's file and put any login in it. A root node's
            // own file that fails this is already reported by the root check below, so it is not
            // named twice.
            if (content is not null && NodeFileWriter.ReadDeclaration(content) is { } account)
            {
                commits = await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken);
                if (commits.Count > 0 && await IsSignedByAsync(repositoryPath, commits[0], publicKeyLine, cancellationToken))
                {
                    DateTimeOffset declaredAt = await CommitTimeAsync(repositoryPath, commits[0], cancellationToken);
                    declarations[nodeId] = new NodeGitHubDeclaration(nodeId, fingerprint, account, declaredAt);
                }
                else if (!namesTrustedRoot)
                {
                    unverified.Add(new UnverifiedLedgerWrite(
                        "node", nodeId, ownerFingerprintLine ?? fingerprint,
                        $"commit {(commits.Count > 0 ? commits[0] : tip)} for {path} declares a GitHub account "
                        + "but is not signed by that node file's own public key"));
                }
            }

            // The display name this node declares (task e6744304) counts under the identical
            // self-signature rule above. Being a label only that never feeds a trust or
            // cross-check decision, though, an improperly signed one simply yields no name here
            // rather than also being named in UnverifiedWrites: there is no trust consequence for
            // this reader to warn about the way a forged GitHub declaration warrants.
            if (content is not null && NodeFileWriter.ReadDisplayName(content) is { HasValue: true } declaredName)
            {
                commits ??= await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken);
                if (commits.Count > 0 && await IsSignedByAsync(repositoryPath, commits[0], publicKeyLine, cancellationToken))
                {
                    // The time this exact name was last set, not simply the newest commit that
                    // touched node.yaml (independent pre-PR review, cycle 1, adversarial lens,
                    // medium): the GitHub declaration shares this same file and is refreshed on its
                    // own schedule, so a GitHub-only rewrite must never make an unrelated, unchanged
                    // display name look newer than a different name set more recently on another node.
                    DateTimeOffset declaredAt = await FieldSettledAtAsync(
                        repositoryPath, path, commits, declaredName.Value,
                        fileContent => NodeFileWriter.ReadDisplayName(fileContent) is { HasValue: true } name ? name.Value : null,
                        cancellationToken);
                    displayNames[nodeId] = new NodeDisplayNameDeclaration(nodeId, fingerprint, declaredName, declaredAt);
                }
            }

            if (!ownerChains.TryGetValue(fingerprint, out TrustedOwner? owner))
            {
                // A fingerprint that names no root this walk trusts.
                continue;
            }

            commits ??= await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken);
            bool signedByALiveRootKey = false;
            foreach (LiveRootKey rootKey in owner.RootKeys)
            {
                if (commits.Count > 0 && await IsSignedByAsync(repositoryPath, commits[0], rootKey.PublicKeyLine, cancellationToken))
                {
                    signedByALiveRootKey = true;
                    break;
                }
            }

            if (signedByALiveRootKey)
            {
                // Self-certification holds — but only actually attach when this node's own current
                // claim still names this exact root: `h9k project join --owner` rewrites a node's
                // own owner_fingerprint field the moment it points itself at a real owner, while
                // RetireSelfRootEverywhereAsync retires that self-created root by adding a
                // retired.yaml beside it, never by removing root.yaml itself — so the retired root
                // still self-certifies above, but this same node's key must not re-attach to it
                // once the node itself has moved on (independent pre-PR review, cycle 1, adversarial
                // lens, medium: a node that retired its own self-created root in favor of a real
                // owner kept re-attaching to the retired root here, so EventCatchUpCoordinator could
                // resolve its role against the retired, non-member root instead of its real owner).
                if (ownerFingerprintLine == fingerprint)
                {
                    ownerChains[fingerprint] = owner with { RootNodeId = nodeId };
                }
            }
            else
            {
                string culpritCommit = commits.Count > 0 ? commits[0] : tip;
                unverified.Add(new UnverifiedLedgerWrite(
                    "node", nodeId, fingerprint,
                    $"commit {culpritCommit} for {path} declares a public key fingerprinting to root {fingerprint} "
                    + "but is not signed by that root's own key"));
            }
        }

        return (unverified, declarations, displayNames);
    }

    /// <summary>The committer time of <paramref name="commit"/>, only ever used to order two declarations of the same account against each other (a rename), never as a trust anchor.</summary>
    private async Task<DateTimeOffset> CommitTimeAsync(string repositoryPath, string commit, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(repositoryPath, ["log", "-1", "--format=%cI", commit], cancellationToken);
        return DateTimeOffset.TryParse(output.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Among <paramref name="commits"/> (newest first, every commit <see cref="CommitsTouchingPathAsync"/>
    /// found touching <paramref name="path"/>), the committer time of the oldest one, contiguous from
    /// the newest, that already read as <paramref name="currentValue"/> through <paramref name="extractValue"/> —
    /// the moment this specific field actually last changed to what it reads as now, rather than
    /// simply the newest commit that happened to touch the file for some unrelated reason. Needed
    /// because more than one field can share a single node file (the GitHub declaration and the
    /// display name, task e6744304): a commit that only refreshes one of them still counts as
    /// "touching the path", and <see cref="CommitTimeAsync"/> alone cannot tell that apart from an
    /// actual change to the field being asked about.
    /// </summary>
    private async Task<DateTimeOffset> FieldSettledAtAsync(
        string repositoryPath, string path, IReadOnlyList<string> commits, string? currentValue,
        Func<string, string?> extractValue, CancellationToken cancellationToken)
    {
        string settledCommit = commits[0];
        for (int index = 1; index < commits.Count; index++)
        {
            string? olderContent = await ReadAtCommitAsync(repositoryPath, commits[index], path, cancellationToken);
            string? olderValue = olderContent is null ? null : extractValue(olderContent);
            if (!string.Equals(olderValue, currentValue, StringComparison.Ordinal))
            {
                break;
            }

            settledCommit = commits[index];
        }

        return await CommitTimeAsync(repositoryPath, settledCommit, cancellationToken);
    }

    /// <summary>
    /// One root's own chain: self-certification (the ref name, the declared public key's own
    /// fingerprint, and the commit that currently produces that content must all agree), then every
    /// vouch or revocation in the ref's own history, oldest first, each one accepted only when its
    /// signer is already a member of the chain being built at that point — the root's own key from
    /// the start, and every node this same walk has already vouched in. Returns a null chain
    /// when the root cannot be self-certified at all: nothing about it is trusted, root key
    /// included — paired with an unverified-write diagnostic naming the specific offending commit
    /// whenever one exists to name, rather than the whole chain silently vanishing with no
    /// diagnostic at all and, for the key-mismatch case specifically, the genesis branch elsewhere
    /// misreporting the honest genesis writer as the cause (independent pre-PR review, cycle 1,
    /// conformance lens, medium).
    /// </summary>
    private async Task<(TrustedOwner? Chain, IReadOnlyList<UnverifiedLedgerWrite> Unverified)> ComputeOwnerChainAsync(
        string repositoryPath, string root, CancellationToken cancellationToken)
    {
        string refName = $"{OwnersRefPrefix}{root}";
        LedgerAppendOnlyFetchResult rootRefFetch = await FetchRefAsync(repositoryPath, refName, cancellationToken);
        string? tip = rootRefFetch.Tip;
        // Kind "ref", never "root": a self-certification or signature failure below also uses
        // ("root", root, root) as its own stream key, and folding a rewind refusal into that same
        // stream would let the last one observed each tick silently overwrite the other, hiding
        // whichever defect lost the race (independent pre-PR review, cycle 1, conformance and
        // adversarial lenses, both low).
        IReadOnlyList<UnverifiedLedgerWrite> refFetchUnverified = rootRefFetch.WasRefused
            ? [new UnverifiedLedgerWrite("ref", refName, root, rootRefFetch.RefusalReason!)]
            : [];
        if (tip is null)
        {
            // No ref, nothing pushed under this root's own namespace yet — there is no commit here
            // to name as an offending write.
            return (null, refFetchUnverified);
        }

        string rootPath = $"owners/{root}/root.yaml";
        string? rootContent = await ReadAtCommitAsync(repositoryPath, tip, rootPath, cancellationToken);
        if (rootContent is null)
        {
            // The ref exists (the namespace was at least touched) but root.yaml itself never landed
            // or was deleted — genuinely nothing here yet, not a write that failed verification.
            return (null, refFetchUnverified);
        }

        // [0], the newest commit reachable from tip that touched root.yaml — the one that actually
        // produced the content just read above — never [^1] (the oldest, the commit that first
        // introduced the path): checking the oldest let an unsigned later commit silently replace
        // root.yaml's own content while this walk kept validating a signature over content nobody
        // still serves, so a currently-served, unsigned mutation was accepted as though it were the
        // original signed one (independent pre-PR review, cycle 1, adversarial lens, medium).
        // Computed up front, before either failure branch below, so both can name the specific
        // commit responsible.
        IReadOnlyList<string> rootCommits = await CommitsTouchingPathAsync(repositoryPath, tip, rootPath, cancellationToken);
        string culpritCommit = rootCommits.Count > 0 ? rootCommits[0] : tip;

        string? publicKeyLine = ExtractQuotedYamlValue(rootContent, "public_key");
        if (publicKeyLine is null || !TryFingerprint(publicKeyLine, out string actualFingerprint) || actualFingerprint != root)
        {
            // Either malformed, or someone else's public key declared under this fingerprint's own
            // namespace — self-certification fails either way, so nothing here is trusted.
            return (null, [
                new UnverifiedLedgerWrite(
                    "root", root, root,
                    $"commit {culpritCommit} for {rootPath} does not self-certify: its own declared public key "
                    + "does not fingerprint back to this root"),
                .. refFetchUnverified]);
        }

        bool rootSelfSigned = rootCommits.Count > 0 && await IsSignedByAsync(repositoryPath, rootCommits[0], publicKeyLine, cancellationToken);

        // A root.yaml that self-certifies (its own declared key fingerprints to `root`, just
        // checked above) but whose commit is NOT signed by that key is exactly the shape a carried
        // bundle produces (task f53fecfd): h9k project join's cross-project root-carry path writes
        // a verbatim copy of the source ledger's own root.yaml here, signed on THIS ledger by the
        // carrying node's own key — nobody on this ledger ever holds the root's own private key at
        // all. Whether that copy is trustworthy is never decided by this commit's own signature (it
        // structurally cannot be); it is decided entirely by whether at least one
        // owners/<root>/carried/*.yaml bundle in this same ref verifies offline against its own
        // embedded, source-signed evidence — replayed below in its own correct chronological
        // position by the identical loop that already replays every ordinary vouch and revocation,
        // rather than pre-checked and discarded: whichever commits it rejects along the way are
        // exactly the diagnostics this root's own final "nothing here is trusted" verdict, when it
        // comes to that, needs to name.
        Dictionary<string, TrustedNode> nodes = [];
        // Every node ever successfully enrolled, current or since revoked — <see
        // cref="TrustedOwner.EverEnrolledNodes"/>'s own doc explains why this never shrinks the way
        // `nodes` does on revocation.
        Dictionary<string, TrustedNode> everEnrolledNodes = [];
        HashSet<string> revokedNodeIds = [];
        List<UnverifiedLedgerWrite> unverified = [];
        string nodesPrefix = $"owners/{root}/nodes/";
        string revokedPrefix = $"owners/{root}/revoked/";
        string carriedPrefix = $"owners/{root}/carried/";
        // Set the moment any carried bundle ever verifies, in its own correct chronological slot —
        // never reset by a later revocation of that same node, the identical reasoning an ordinary
        // self-established root's own key always keeps "existing" as a root even once every node it
        // ever vouched is revoked. A carried root that later revokes its only carried node still
        // returns an established (non-null) chain here, with an empty Nodes list, rather than
        // retroactively un-establishing the root itself.
        bool establishedByCarry = false;
        // Every carried node id this replay has already SUCCESSFULLY established (never merely
        // touched — see the gate below) — a revoked node still legitimately holds its own private
        // key and its embedded evidence never changes, so without this a revoked carried node
        // could simply push a fresh commit re-touching its own
        // owners/<root>/carried/<node-id>.yaml (content unchanged or not) and have
        // VerifyCarriedRecordAsync re-verify and reinstate it, self-signed by the very key the
        // revocation was supposed to revoke — the identical resurrection an ordinary vouch cannot
        // pull off, because a revoked node is removed from `nodes` and so can no longer satisfy
        // the "signed by root or an already-enrolled node" gate below (independent pre-PR review,
        // adversarial lens, high). The carried record's own embedded evidence is still what
        // authorizes the FIRST successful establishment of a given node id — nothing local is
        // enrolled yet to sign it — but every commit after that first success needs the identical
        // live-chain authorization an ordinary re-vouch already needs.
        HashSet<string> carriedPathsEstablished = [];

        IReadOnlyList<string> allCommits = await CommitsOldestFirstAsync(repositoryPath, tip, cancellationToken);
        // Cached here, once per commit, rather than re-run inside ComputeSuccessionAsync below: that
        // second pass walks this identical commit list a second time, and a `git diff-tree` per
        // commit is not free — this reader's own cost already scales with ref history length, and
        // succession would otherwise double it for every root regardless of whether that root has
        // ever written a single successor, rotation, or revoked-successor record.
        Dictionary<string, IReadOnlyList<string>> changedPathsByCommit = [];
        foreach (string commit in allCommits)
        {
            IReadOnlyList<string> changedPaths = await ChangedPathsAsync(repositoryPath, commit, cancellationToken);
            changedPathsByCommit[commit] = changedPaths;
            foreach (string path in changedPaths)
            {
                if (path.StartsWith(carriedPrefix, StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    string carriedNodeId = path[carriedPrefix.Length..^".yaml".Length];
                    if (carriedNodeId.IsBlank())
                    {
                        continue;
                    }

                    // Gated on a PRIOR SUCCESSFUL establishment of this exact node id (never merely
                    // a prior touch of the path: an earlier commit that failed verification — a
                    // stranger's garbage, or this same carrying node's own malformed retry — must
                    // never block a later, genuinely valid carry for the identical node id from
                    // still self-authorizing on its own embedded evidence, since nothing was ever
                    // actually established for this node yet to require re-authorization from;
                    // independent review finding, round two: the first draft of this fix gated on
                    // "already touched," which let one bad early commit at a node id's own path
                    // permanently lock out every later, legitimate one) OR this exact node id
                    // already being revoked at this point in the replay: a node vouched the
                    // ORDINARY way (never carried before) and then revoked still holds its own
                    // private key and its own embedded carry evidence never changes, so without this
                    // second condition its first-ever carried record would self-authorize on that
                    // same untouched evidence and resurrect it, erasing the revocation exactly the
                    // way the carriedPathsEstablished condition alone already prevents for a node
                    // that was carried before (independent pre-PR review, adversarial lens, high).
                    if (carriedPathsEstablished.Contains(carriedNodeId) || revokedNodeIds.Contains(carriedNodeId))
                    {
                        List<string> reAuthorizeCandidates = [publicKeyLine, .. nodes.Values.Select(node => node.PublicKeyLine)];
                        bool reAuthorized = false;
                        foreach (string candidate in reAuthorizeCandidates)
                        {
                            if (await IsSignedByAsync(repositoryPath, commit, candidate, cancellationToken))
                            {
                                reAuthorized = true;
                                break;
                            }
                        }

                        if (!reAuthorized)
                        {
                            unverified.Add(new UnverifiedLedgerWrite(
                                "carried", carriedNodeId, root,
                                $"commit {commit} for {path} targets a node id that is already revoked or was "
                                + $"already carry-established, and is not signed by root {root} or any node "
                                + "currently enrolled in it"));
                            continue;
                        }
                    }

                    (TrustedNode? carriedNode, UnverifiedLedgerWrite? failure, UnverifiedLedgerWrite? refRefusal) =
                        await VerifyCarriedRecordAsync(repositoryPath, commit, path, carriedNodeId, root, publicKeyLine, cancellationToken);
                    if (refRefusal is not null)
                    {
                        unverified.Add(refRefusal);
                    }

                    if (failure is not null)
                    {
                        unverified.Add(failure);
                        continue;
                    }

                    if (carriedNode is not null)
                    {
                        nodes[carriedNodeId] = carriedNode;
                        everEnrolledNodes[carriedNodeId] = carriedNode;
                        revokedNodeIds.Remove(carriedNodeId);
                        establishedByCarry = true;
                        carriedPathsEstablished.Add(carriedNodeId);
                    }

                    continue;
                }

                bool isRevoke;
                string nodeId;
                if (path.StartsWith(nodesPrefix, StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    nodeId = path[nodesPrefix.Length..^".yaml".Length];
                    isRevoke = false;
                }
                else if (path.StartsWith(revokedPrefix, StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    nodeId = path[revokedPrefix.Length..^".yaml".Length];
                    isRevoke = true;
                }
                else
                {
                    continue;
                }

                if (nodeId.IsBlank())
                {
                    continue;
                }

                List<string> candidateKeys = [publicKeyLine, .. nodes.Values.Select(node => node.PublicKeyLine)];
                bool signedByEnrolledNode = false;
                foreach (string candidate in candidateKeys)
                {
                    if (await IsSignedByAsync(repositoryPath, commit, candidate, cancellationToken))
                    {
                        signedByEnrolledNode = true;
                        break;
                    }
                }

                // Any other writer is refused — never applied to the chain being built. The file
                // may sit in the tree, written by whoever pushed it, but it never becomes part of
                // what this project trusts (idea 202383dc, T1 criterion 1) — recorded here rather
                // than silently dropped, so a caller can name the writer (independent pre-PR
                // review, cycle 1, conformance lens, medium).
                if (!signedByEnrolledNode)
                {
                    unverified.Add(new UnverifiedLedgerWrite(
                        isRevoke ? "revocation" : "vouch", nodeId, root,
                        $"commit {commit} is not signed by root {root} or any node currently enrolled in it"));
                    continue;
                }

                if (isRevoke)
                {
                    nodes.Remove(nodeId);
                    // Recorded even for a node id nothing above ever vouched (the root's own node
                    // id, which has no owners/<root>/nodes/<id>.yaml entry to remove) — that is the
                    // only record its revocation ever leaves, and FleetNodeIds is the sole reader.
                    revokedNodeIds.Add(nodeId);
                    continue;
                }

                string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
                string? nodePublicKey = content is null ? null : ExtractQuotedYamlValue(content, "public_key");
                if (nodePublicKey is null || !TryFingerprint(nodePublicKey, out string nodeFingerprint))
                {
                    continue;
                }

                DateTimeOffset issuedAt = ParseIssuedAt(content);
                // Later of vouch/revocation in ref commit order wins by construction: this walk
                // processes commits oldest to newest and simply overwrites whichever state a
                // node-id last held, so a surviving node vouching again after a bad revocation
                // restores it here exactly as idea 202383dc's own model describes.
                TrustedNode trustedNode = new(nodeId, nodePublicKey, nodeFingerprint, issuedAt);
                nodes[nodeId] = trustedNode;
                everEnrolledNodes[nodeId] = trustedNode;
                revokedNodeIds.Remove(nodeId);
            }
        }

        // Nothing here is trusted when root.yaml's own commit is not signed by the root's own key
        // AND no carried bundle in the loop above ever verified either — the identical "nothing
        // established" verdict the pre-carry code gave whenever the plain signature check alone
        // failed, just reached after the one replay loop both paths now share instead of a second,
        // discarded pre-check.
        if (!rootSelfSigned && !establishedByCarry)
        {
            return (null, [
                new UnverifiedLedgerWrite(
                    "root", root, root,
                    $"commit {culpritCommit} for {rootPath} is not signed by that root's own key, and no "
                    + $"owners/{root}/carried/*.yaml bundle verifies it either"),
                .. unverified, .. refFetchUnverified]);
        }

        // Pass 1 of the succession model (idea 6be68ee2): a second walk of this identical root ref,
        // over successors/, rotations/, and revoked-successors/ alone, now that `nodes` above is
        // final — "a successor record counts only while its node is in Nodes" reads the fleet as it
        // stands now, the same live-state rule every other write in this reader already follows,
        // never a snapshot pinned to wherever the replay happened to be when it saw that record.
        (IReadOnlyList<LiveRootKey> rootKeys, IReadOnlyList<string> successorNodeIds, IReadOnlyList<UnverifiedLedgerWrite> successionUnverified) =
            await ComputeSuccessionAsync(
                repositoryPath, root, publicKeyLine, allCommits, changedPathsByCommit, nodes, everEnrolledNodes, revokedNodeIds,
                cancellationToken);

        return (
            new TrustedOwner(
                root, publicKeyLine, [.. nodes.Values], RevokedNodeIds: revokedNodeIds,
                EverEnrolledNodes: [.. everEnrolledNodes.Values], RootKeys: rootKeys, SuccessorNodeIds: successorNodeIds),
            [.. unverified, .. successionUnverified, .. refFetchUnverified]);
    }

    /// <summary>
    /// Pass 1 of the succession model (idea 6be68ee2, HALL9K-P2P-DESIGN.md §6, decision 29):
    /// replays <paramref name="allCommits"/> oldest first, a second time, over
    /// <c>owners/&lt;root&gt;/successors/</c>, <c>owners/&lt;root&gt;/rotations/</c>, and
    /// <c>owners/&lt;root&gt;/revoked-successors/</c> alone, and derives the final ranked root-key
    /// set: K0 (<paramref name="rootPublicKeyLine"/>) first, then every rotation that actually
    /// validated, in the order it landed.
    /// <list type="bullet">
    /// <item><b>A successor record</b> (<c>successors/&lt;node-id&gt;.yaml</c>: the node id and the
    /// key already vouched for it) counts only when it is signed by a key already live in the chain
    /// at that point, and only when the named node is, right now — this walk's own final
    /// <paramref name="nodes"/>, not a snapshot — actually vouched under the identical key it
    /// declares; a node-signed one, or one naming a node that never vouched that key, is refused and
    /// named.</item>
    /// <item><b>A rotation</b> (<c>rotations/&lt;n&gt;.yaml</c>: the promoting node id, its key, and
    /// the exact key it supersedes) counts only when its promoter is currently a listed successor
    /// under that identical key, currently vouched under it, signed the commit itself with that same
    /// key, and names the CURRENT top of the chain being built as the key it supersedes — never an
    /// earlier one. Since a write here can only ever move that top forward by exactly one step, the
    /// first rotation to land always wins: whichever one lands second, however validly signed, now
    /// names a key that is no longer current and is refused as stale.</item>
    /// <item>A successor or revoked-successor record naming a node that WAS legitimately vouched
    /// under the exact key it declares, but has since left <paramref name="nodes"/> through an
    /// ordinary, unrelated <c>h9k node revoke</c> (<paramref name="revokedNodeIds"/>,
    /// <paramref name="everEnrolledNodes"/>) simply stops counting — never named as an unverifiable
    /// write: the record was honest when it landed, and the live-state read this whole pass already
    /// applies to every other check here means its node's own departure is what silently retires it,
    /// not a sign of forgery (independent pre-PR review, cycle 1, both lenses, medium — an owner's
    /// own routine revoke of a node it once named a successor otherwise left two permanent yellow
    /// lines in <c>h9k status</c> that no later read ever clears).</item>
    /// <item><b>A revoked-successor record</b> (<c>revoked-successors/&lt;node-id&gt;.yaml</c>)
    /// removes a node's current candidacy, or — when that node's own rotation already landed — that
    /// key and everything the chain built on top of it, but only when signed by a key ranked ABOVE
    /// the node it targets: any key currently in the chain when the target is still only a candidate
    /// (no rank of its own yet to be "above"), or specifically one of the keys ranked before the
    /// target's own position once it has rotated in. K0 itself can never be a target — nothing names
    /// K0 by node id, since it is <c>root.yaml</c>'s own key, not a promotion.</item>
    /// </list>
    /// A revocation that lands before the rotation it names simply means that rotation's own
    /// "current top" check fails once it is reached — it never validates, exactly as if it had never
    /// been written. Every rejection is recorded rather than silently dropped, the identical
    /// "name the writer" contract every other check in this class already keeps.
    /// </summary>
    private async Task<(IReadOnlyList<LiveRootKey> RootKeys, IReadOnlyList<string> SuccessorNodeIds, IReadOnlyList<UnverifiedLedgerWrite> Unverified)>
        ComputeSuccessionAsync(
            string repositoryPath, string root, string rootPublicKeyLine, IReadOnlyList<string> allCommits,
            IReadOnlyDictionary<string, IReadOnlyList<string>> changedPathsByCommit,
            IReadOnlyDictionary<string, TrustedNode> nodes, IReadOnlyDictionary<string, TrustedNode> everEnrolledNodes,
            IReadOnlySet<string> revokedNodeIds, CancellationToken cancellationToken)
    {
        string successorsPrefix = $"owners/{root}/successors/";
        string rotationsPrefix = $"owners/{root}/rotations/";
        string revokedSuccessorsPrefix = $"owners/{root}/revoked-successors/";
        const string suffix = ".yaml";

        List<LiveRootKey> chain = [new LiveRootKey(rootPublicKeyLine, root, null)];
        Dictionary<string, string> successorCandidates = [];
        List<UnverifiedLedgerWrite> unverified = [];

        foreach (string commit in allCommits)
        {
            foreach (string path in changedPathsByCommit[commit])
            {
                if (path.StartsWith(successorsPrefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    string nodeId = path[successorsPrefix.Length..^suffix.Length];
                    if (nodeId.IsBlank())
                    {
                        continue;
                    }

                    string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
                    if (content is null)
                    {
                        // A deletion — nothing this task ever writes, but nothing to apply either way.
                        continue;
                    }

                    string? declaredKey = ExtractQuotedYamlValue(content, "public_key");
                    if (declaredKey is null)
                    {
                        continue;
                    }

                    bool signedByALiveRootKey = false;
                    foreach (LiveRootKey rootKey in chain)
                    {
                        if (await IsSignedByAsync(repositoryPath, commit, rootKey.PublicKeyLine, cancellationToken))
                        {
                            signedByALiveRootKey = true;
                            break;
                        }
                    }

                    if (!signedByALiveRootKey)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "successor", nodeId, root,
                            $"commit {commit} for {path} is not signed by any of root {root}'s own live keys"));
                        continue;
                    }

                    if (!nodes.TryGetValue(nodeId, out TrustedNode? vouchedNode) || vouchedNode.PublicKeyLine != declaredKey)
                    {
                        // Valid when written but the node has since left Nodes (an ordinary,
                        // unrelated `h9k node revoke`) is a live-state read, not a forgery: the
                        // record simply stops counting as a successor candidate, the identical
                        // "the chain's live state, recomputed fresh on every call" rule this
                        // reader's own doc already states for membership writes — reporting it as
                        // an unverifiable writer left a permanent, unresolvable yellow line in
                        // h9k status for every owner who ever runs an ordinary revoke on a node
                        // they once vouched a successor record for (independent pre-PR review,
                        // cycle 1, both lenses, medium).
                        if (revokedNodeIds.Contains(nodeId)
                            && everEnrolledNodes.TryGetValue(nodeId, out TrustedNode? everNode) && everNode.PublicKeyLine == declaredKey)
                        {
                            continue;
                        }

                        unverified.Add(new UnverifiedLedgerWrite(
                            "successor", nodeId, root,
                            $"commit {commit} for {path} names a node that is not currently vouched into root {root}'s own "
                            + "fleet under this exact key"));
                        continue;
                    }

                    successorCandidates[nodeId] = declaredKey;
                    continue;
                }

                if (path.StartsWith(rotationsPrefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    string sequence = path[rotationsPrefix.Length..^suffix.Length];
                    if (sequence.IsBlank())
                    {
                        continue;
                    }

                    string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
                    if (content is null)
                    {
                        continue;
                    }

                    string? promotingNodeId = ExtractQuotedYamlValue(content, "node_id");
                    string? promotingKey = ExtractQuotedYamlValue(content, "public_key");
                    string? supersededKey = ExtractQuotedYamlValue(content, "supersedes_public_key");
                    if (promotingNodeId is null || promotingKey is null || supersededKey is null)
                    {
                        continue;
                    }

                    if (!successorCandidates.TryGetValue(promotingNodeId, out string? listedKey) || listedKey != promotingKey)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "rotation", promotingNodeId, root,
                            $"commit {commit} for {path} promotes a node that is not currently a listed successor under "
                            + "this exact key"));
                        continue;
                    }

                    if (!nodes.TryGetValue(promotingNodeId, out TrustedNode? vouchedNode) || vouchedNode.PublicKeyLine != promotingKey)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "rotation", promotingNodeId, root,
                            $"commit {commit} for {path} promotes a node that is not currently vouched into root {root}'s "
                            + "own fleet under this exact key"));
                        continue;
                    }

                    if (!await IsSignedByAsync(repositoryPath, commit, promotingKey, cancellationToken))
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "rotation", promotingNodeId, root,
                            $"commit {commit} for {path} is not signed by the promoting node's own vouched key"));
                        continue;
                    }

                    if (supersededKey != chain[^1].PublicKeyLine)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "rotation", promotingNodeId, root,
                            $"commit {commit} for {path} supersedes a key that is no longer the current top of root "
                            + $"{root}'s own root-key chain — stale, ignored"));
                        continue;
                    }

                    chain.Add(new LiveRootKey(promotingKey, vouchedNode.Fingerprint, promotingNodeId));
                    successorCandidates.Remove(promotingNodeId);
                    continue;
                }

                if (path.StartsWith(revokedSuccessorsPrefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    string nodeId = path[revokedSuccessorsPrefix.Length..^suffix.Length];
                    if (nodeId.IsBlank())
                    {
                        continue;
                    }

                    string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
                    if (content is null)
                    {
                        continue;
                    }

                    int chainIndex = chain.FindIndex(key => key.IntroducedByNodeId == nodeId);
                    bool isPendingCandidate = chainIndex < 0 && successorCandidates.ContainsKey(nodeId);
                    if (chainIndex < 0 && !isPendingCandidate)
                    {
                        // An ordinary node revoke's own cleanup write (h9k node revoke pairs
                        // `revoked/<id>.yaml` with this record whenever the revoking key is itself a
                        // live root key): the node's own successor candidacy already lapsed the
                        // moment it left Nodes above, so there is nothing left here to revoke, and
                        // that is not a forgery to report — the identical live-state reasoning the
                        // successor check above already applies (independent pre-PR review, cycle 1,
                        // both lenses, medium).
                        if (revokedNodeIds.Contains(nodeId) && everEnrolledNodes.ContainsKey(nodeId))
                        {
                            continue;
                        }

                        unverified.Add(new UnverifiedLedgerWrite(
                            "revoked-successor", nodeId, root,
                            $"commit {commit} for {path} names a node with no live successor candidacy or rotation to revoke"));
                        continue;
                    }

                    // A pending candidate has no rank of its own yet, so every currently live root
                    // key ranks "above" it; a already-rotated-in key only yields to a strictly
                    // higher-ranked one — nothing revokes K0 itself, since chainIndex is never 0 for
                    // any node id (K0's own IntroducedByNodeId is always null).
                    IEnumerable<LiveRootKey> eligibleRevokers = chainIndex >= 0 ? chain.Take(chainIndex) : chain;
                    bool signedByAHigherRankedKey = false;
                    foreach (LiveRootKey candidate in eligibleRevokers)
                    {
                        if (await IsSignedByAsync(repositoryPath, commit, candidate.PublicKeyLine, cancellationToken))
                        {
                            signedByAHigherRankedKey = true;
                            break;
                        }
                    }

                    if (!signedByAHigherRankedKey)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "revoked-successor", nodeId, root,
                            $"commit {commit} for {path} is not signed by a root key ranked above the successor it targets"));
                        continue;
                    }

                    successorCandidates.Remove(nodeId);
                    if (chainIndex >= 0)
                    {
                        chain.RemoveRange(chainIndex, chain.Count - chainIndex);
                    }

                    continue;
                }
            }
        }

        return (chain, [.. successorCandidates.Keys], unverified);
    }

    /// <summary>
    /// Verifies one <c>owners/&lt;root&gt;/carried/&lt;node-id&gt;.yaml</c> bundle entirely offline
    /// (task f53fecfd, criterion 2), against exactly four checks — every one has to hold, or the
    /// bundle establishes nothing and is recorded rather than silently dropped:
    /// <list type="number">
    /// <item>the embedded <c>root.yaml</c>'s own declared public key fingerprints to <paramref name="root"/>;</item>
    /// <item>the embedded root commit is SSH-signed by that key;</item>
    /// <item>the embedded vouch commit is signed by the root key — the only key a single carried
    /// bundle could ever transitively enrol before the vouch itself is checked, since nothing else
    /// this bundle carries is trusted yet at that point — AND its own signed commit message names
    /// both the node id and the fingerprint of the exact key this bundle declares, so the key this
    /// bundle enrols is the key the root actually vouched, not merely a field the bundle's own
    /// writer set;</item>
    /// <item>the carried node's own declared public key equals SOME commit in THIS ledger's own
    /// <c>nodes/&lt;node-id&gt;/node.yaml</c> history — not necessarily the current one — and that
    /// commit is self-signed by that same key — the binding that ties this bundle to whoever was
    /// actually running the join on <em>this</em> machine at the time: only the holder of that
    /// node's own private key can have produced a self-signed <c>node.yaml</c> declaring it here
    /// (idea's own origin note: "the bundle binds to the carrying node because only the holder of
    /// that node private key can self-announce with the same public key on B"). Checked against the
    /// whole history rather than pinned to the current tip because a carried root, unlike an
    /// ordinary one, has no self-signed <c>root.yaml</c> fallback on this ledger: pinning to the
    /// current commit would let an unrelated later key rotation on this exact node retroactively
    /// cancel a carry that was genuine when it happened (independent pre-PR review, cycle 1,
    /// conformance lens, medium).
    /// </item>
    /// </list>
    /// Deliberately does not walk either embedded commit's own tree to confirm it produced the
    /// embedded <c>root.yaml</c>/vouch text byte-for-byte (that would need this method to also
    /// inject and resolve every intermediate tree object down to the blob, not just the commit
    /// object) — check 3's own commit-message binding, below, is the cheaper bar this bundle
    /// actually has to clear instead. The known, accepted limit that leaves ("a carried root is
    /// established by a vouched node, not by the root key") is recorded in the Decisions Log rather
    /// than engineered around here.
    /// </summary>
    private async Task<(TrustedNode? Node, UnverifiedLedgerWrite? Failure, UnverifiedLedgerWrite? RefRefusal)> VerifyCarriedRecordAsync(
        string repositoryPath, string commit, string path, string nodeId, string root, string rootPublicKeyLine,
        CancellationToken cancellationToken)
    {
        string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
        if (content is null)
        {
            // A deletion (or, on the pre-check's own replay, simply not the content this commit
            // happens to hold) — nothing to verify or complain about.
            return (null, null, null);
        }

        string? nodePublicKey = ExtractQuotedYamlValue(content, "node_public_key");
        string? rootYamlBase64 = ExtractQuotedYamlValue(content, "root_yaml_base64");
        string? rootCommitBase64 = ExtractQuotedYamlValue(content, "root_commit_base64");
        string? vouchCommitBase64 = ExtractQuotedYamlValue(content, "vouch_commit_base64");
        if (nodePublicKey is null || rootYamlBase64 is null || rootCommitBase64 is null || vouchCommitBase64 is null)
        {
            return (null, Unverified("is missing a required field"), null);
        }

        // Check 1: the embedded root.yaml's own declared public key fingerprints to `root`.
        if (!TryDecodeBase64(rootYamlBase64, out string embeddedRootYaml))
        {
            return (null, Unverified("carries an embedded root.yaml that is not valid base64"), null);
        }

        string? embeddedRootPublicKey = ExtractQuotedYamlValue(embeddedRootYaml, "public_key");
        if (embeddedRootPublicKey is null || !TryFingerprint(embeddedRootPublicKey, out string embeddedFingerprint)
            || embeddedFingerprint != root)
        {
            return (null, Unverified("carries an embedded root.yaml that does not self-certify to this root"), null);
        }

        // Check 2: the embedded root commit is SSH-signed by that key.
        if (!TryDecodeBase64(rootCommitBase64, out string rootCommitBytes)
            || !await IsSignedByRawBytesAsync(repositoryPath, rootCommitBytes, embeddedRootPublicKey, cancellationToken))
        {
            return (null, Unverified("carries an embedded root commit that is not signed by the root's own key"), null);
        }

        if (!TryFingerprint(nodePublicKey, out string nodeFingerprint))
        {
            return (null, Unverified("declares a node public key that is malformed"), null);
        }

        // Check 3: the embedded vouch commit is signed by the root key (single-hop, see the doc
        // above), AND its own raw bytes — the exact payload that signature covers, never something
        // this method trusts on faith — name BOTH this specific node id AND the fingerprint of the
        // exact key this bundle declares in `node_public_key`. Every vouch this platform ever
        // writes carries both in its own commit message (NodeVouchCommand: "Vouch node {id} key
        // {fingerprint}"; InviteSweepEngine: "Vouch node {id} key {fingerprint} (invite)"), so a
        // genuine vouch commit for this node id but a DIFFERENT key — an attacker who copied a
        // genuine bundle and substituted their own key into node_public_key, keeping the genuine
        // root-signed root/vouch commits — can never satisfy this, and neither can any other commit
        // root ever happened to sign for an unrelated reason, root.yaml's own establishing commit or
        // a "Revoke node {id}" commit included, both of which name the node id but never this
        // binding (independent pre-PR review, cycle 1, both lenses, high: node_public_key was
        // previously never tied to what the root actually vouched — check 4 below only ever compared
        // it against this ledger's own current node.yaml, which anyone who can push here can
        // overwrite and self-sign with any key they hold).
        string vouchMarker = $"Vouch node {nodeId} key {nodeFingerprint}";
        if (!TryDecodeBase64(vouchCommitBase64, out string vouchCommitBytes)
            || !await IsSignedByRawBytesAsync(repositoryPath, vouchCommitBytes, embeddedRootPublicKey, cancellationToken)
            || !vouchCommitBytes.Contains(vouchMarker, StringComparison.OrdinalIgnoreCase))
        {
            return (null, Unverified(
                "carries an embedded vouch commit that is not signed by the root's own key, or is signed but never names both this node and this exact key"),
                null);
        }

        // Check 4: some commit in this ledger's own nodes/<id>/node.yaml HISTORY — never only its
        // current tip — declared the carried key and is self-signed by that same key: a
        // belt-and-suspenders binding to whoever actually ran the join on this exact machine, on top
        // of check 3's own binding to what the root actually vouched. Walking the whole history
        // rather than pinning to the current commit matters because, unlike an ordinary root, a
        // carried root has no self-signed root.yaml fallback on this ledger to fall back to: a later,
        // unrelated key rotation on this exact node (a lost private key after a reinstall, say) would
        // otherwise retroactively cancel every reader's view of a carry that was genuine at the time
        // it happened, with no path back (independent pre-PR review, cycle 1, conformance lens,
        // medium — origin: the previous check compared only against node.yaml's current content).
        string nodeRefName = $"{NodesRefPrefix}{nodeId}";
        string nodePath = $"nodes/{nodeId}/node.yaml";
        LedgerAppendOnlyFetchResult nodeRefFetch = await FetchRefAsync(repositoryPath, nodeRefName, cancellationToken);

        // A rewind or side merge on the node's own ledger ref is recorded, never fail-closed: the
        // verified tip nodeRefFetch.Tip carries on a refusal is exactly what this node already
        // trusted, so check 4 walks it the same as any ordinary fetch would (independent pre-PR
        // review, cycle 1, conformance lens, medium — the previous fail-closed reading let anyone who
        // can force-push that ref revoke a carry this node had already established, by rewinding past
        // the very commit that established it).
        UnverifiedLedgerWrite? refRefusal = nodeRefFetch.WasRefused
            ? new UnverifiedLedgerWrite("ref", nodeRefName, root, nodeRefFetch.RefusalReason!)
            : null;

        string? localTip = nodeRefFetch.Tip;
        IReadOnlyList<string> localNodeCommits = localTip is null
            ? []
            : await CommitsTouchingPathAsync(repositoryPath, localTip, nodePath, cancellationToken);

        bool matchedAnyHistoricalCommit = false;
        foreach (string candidateCommit in localNodeCommits)
        {
            string? candidateContent = await ReadAtCommitAsync(repositoryPath, candidateCommit, nodePath, cancellationToken);
            string? candidatePublicKey = candidateContent is null ? null : ExtractQuotedYamlValue(candidateContent, "public_key");
            if (candidatePublicKey == nodePublicKey
                && await IsSignedByAsync(repositoryPath, candidateCommit, candidatePublicKey, cancellationToken))
            {
                matchedAnyHistoricalCommit = true;
                break;
            }
        }

        if (!matchedAnyHistoricalCommit)
        {
            return (null, Unverified(
                $"declares a node public key that no commit in this ledger's own {nodePath} history self-signs"),
                refRefusal);
        }

        return (new TrustedNode(nodeId, nodePublicKey, nodeFingerprint, ParseIssuedAt(content)), null, refRefusal);

        UnverifiedLedgerWrite Unverified(string reason) => new("carried", nodeId, root, $"commit {commit} for {path} {reason}");
    }

    /// <summary>
    /// Verifies raw commit bytes an offline bundle embedded, without ever fetching from wherever
    /// they came from: injects them as a loose object into THIS repository
    /// (<c>git hash-object -w -t commit</c>) and runs the identical <see cref="IsSignedByAsync"/>
    /// check against the sha that injection computes — a git object is content-addressed, so the
    /// injected object's own sha is identical to whatever it was in the repository it was read from,
    /// and <c>git verify-commit</c> needs nothing beyond the commit object itself (never its tree or
    /// parents) to check a signature over it.
    /// </summary>
    private async Task<bool> IsSignedByRawBytesAsync(
        string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken)
    {
        if (!HasSshSignatureHeader(rawCommitBytes))
        {
            return false;
        }

        string tempCommitFile = Path.Combine(Path.GetTempPath(), $"h9k-carried-commit-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(tempCommitFile, rawCommitBytes, cancellationToken);
            ProcessResult hashResult = await runner(
                "git", ["hash-object", "-w", "-t", "commit", tempCommitFile], repositoryPath, cancellationToken);
            if (hashResult.ExitCode != 0)
            {
                return false;
            }

            string injectedSha = hashResult.StandardOutput.Trim();
            return await IsSignedByAsync(repositoryPath, injectedSha, publicKeyLine, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(tempCommitFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file; nothing downstream reads it again.
            }
        }
    }

    private static bool TryDecodeBase64(string base64, out string decoded)
    {
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Replays <c>refs/hall9k/ledger/members</c> oldest to newest. The very first commit the ref's
    /// own history ever holds that touches a <c>members/*.yaml</c> path is genesis: accepted only
    /// when signed by that root's own key or any node its chain has EVER enrolled, current or since
    /// revoked (<see cref="IsAuthorizedForGenesisAsync"/> — a carried ledger's genesis commit can
    /// only ever be signed by the carrying node itself, and genesis has no later re-vouch that could
    /// ever restore it once lost), unconditionally the project's first owner-role member either way
    /// — win or lose, that slot is spent once. Every later write needs its signer to belong, right now, to a currently
    /// Owner-role member's own chain (<see cref="IsAuthorizedByOwnerChainAsync"/>) — the chain's
    /// live state at read time, the one <paramref name="ownerChains"/> already holds, never a
    /// snapshot pinned to this commit's own claimed committer date. That date is a field its writer
    /// freely chooses, so it is never a trustworthy anchor for anything (independent pre-PR review,
    /// cycle 1; ruled by the window, 2026-09-13): a members-ref write is authorized or refused by
    /// one and the same rule regardless of when it landed, first from a given node's key or
    /// hundredth. Consequence, accepted rather than deferred: once a node is revoked, every
    /// membership write it ever signed — including one made while it was still legitimately
    /// enrolled — stops being authorized on the next read, and a later re-vouch of that same node
    /// restores them all again, exactly the latest-of-vouch-or-revocation rule the owner chain
    /// itself already applies.
    /// <para>
    /// Anything unauthorized is ignored, including a later self-claimed owner with no vouch (idea
    /// 202383dc, T1 criterion 2) — and recorded in the returned unverified list rather than silently
    /// dropped.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<ProjectMember> Members, IReadOnlyList<UnverifiedLedgerWrite> Unverified,
        string? GenesisRootFingerprint, string? ProjectKey)> ComputeMembersAsync(
        string repositoryPath, IReadOnlyDictionary<string, TrustedOwner> ownerChains, CancellationToken cancellationToken)
    {
        LedgerAppendOnlyFetchResult membersRefFetch = await FetchRefAsync(repositoryPath, MembersRefName, cancellationToken);
        List<UnverifiedLedgerWrite> unverified = [];
        if (membersRefFetch.WasRefused)
        {
            unverified.Add(new UnverifiedLedgerWrite("ref", MembersRefName, string.Empty, membersRefFetch.RefusalReason!));
        }

        string? tip = membersRefFetch.Tip;
        if (tip is null)
        {
            return ([], unverified, null, null);
        }

        const string prefix = "members/";
        const string suffix = ".yaml";
        Dictionary<string, ProjectMember> current = [];
        bool genesisDecided = false;
        string? genesisRootFingerprint = null;
        // The project's own key (idea 202383dc, M2), tracked live during this same replay rather
        // than read back from whatever members/<genesis>.yaml happens to hold at the ref's tip: a
        // tip read trusts that file's current content unconditionally, so anyone who can push
        // refs/hall9k/ledger/members (even a write the replay above refuses and records into
        // UnverifiedWrites) could set, change, or (by deleting the genesis member entirely, an
        // ownership handoff this ledger already supports) permanently erase the key every node then
        // computes (independent pre-PR review, cycle 1, conformance and adversarial lenses, both
        // high). Set only from the genesis commit's own self-signed content, or from a later commit
        // to that identical path this replay has already verified was authorized by the genesis
        // root's own chain specifically, never any other currently-owner-role member's, and never
        // reassigned once set, so neither a forged rewrite nor the genesis member's own later
        // removal can ever change or lose it again.
        string? projectKey = null;

        foreach (string commit in await CommitsOldestFirstAsync(repositoryPath, tip, cancellationToken))
        {
            foreach (string path in await ChangedPathsAsync(repositoryPath, commit, cancellationToken))
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string fingerprint = path[prefix.Length..^suffix.Length];
                if (fingerprint.IsBlank())
                {
                    continue;
                }

                string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
                bool isDeletion = content is null;

                if (!genesisDecided)
                {
                    genesisDecided = true;
                    // Named regardless of whether genesis actually self-certifies below: this is a
                    // fact about the ref's own immutable first commit, not a verdict on trust, and
                    // every node fetching the identical ref lands on the identical value (TrustChain's
                    // own doc: "the project's own key, derived from the ledger").
                    genesisRootFingerprint = fingerprint;
                    // Signed by the root's own key OR by any node this same root's chain has EVER
                    // enrolled, current or since revoked — never merely "self-signed by the root's
                    // own key" alone (task f53fecfd, criterion 3): a carried record establishes root
                    // R with no local node ever holding R's own private key, so the genesis member
                    // commit for R can only ever be signed by the carried node itself.
                    // IsAuthorizedForGenesisAsync is deliberately NOT IsAuthorizedByOwnerChainAsync,
                    // the identical-looking rule every later membership write uses: that rule checks
                    // only currently-enrolled nodes, which is the correct, accepted trade-off for an
                    // ordinary write (a later re-vouch can always restore it) but not for genesis on
                    // a carried ledger, where the carrying node is the ONLY signer genesis can ever
                    // have. Revoking that one node — an otherwise routine h9k node revoke — would
                    // otherwise permanently wipe this project's own genesis member and, with it, the
                    // project key nothing else can ever remint (independent pre-PR review, cycle 1,
                    // conformance lens, medium). Genesis differs from every later write only in being
                    // unconditionally accepted once authorized, no existing owner-role member
                    // required first.
                    if (content is not null
                        && ownerChains.TryGetValue(fingerprint, out TrustedOwner? selfOwner)
                        && await IsAuthorizedForGenesisAsync(repositoryPath, commit, selfOwner, cancellationToken))
                    {
                        current[fingerprint] = new ProjectMember(fingerprint, MembershipRole.Owner, ParseIssuedAt(content));
                        projectKey ??= ExtractQuotedYamlValue(content, "project_key");
                    }
                    else if (!isDeletion)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "membership", fingerprint, fingerprint,
                            $"genesis commit {commit} for {fingerprint} is not signed by that root's own key or a node it enrols"));
                    }

                    // Whether genesis succeeded or not, the bootstrap exception is spent: only
                    // the ref's own literal first members-touching commit ever gets it.
                    continue;
                }

                bool authorized = false;
                foreach (ProjectMember member in current.Values)
                {
                    if (member.Role != MembershipRole.Owner)
                    {
                        continue;
                    }

                    if (!ownerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner))
                    {
                        continue;
                    }

                    if (await IsAuthorizedByOwnerChainAsync(repositoryPath, commit, owner, cancellationToken))
                    {
                        authorized = true;
                        break;
                    }
                }

                if (!authorized)
                {
                    unverified.Add(new UnverifiedLedgerWrite(
                        "membership", fingerprint, fingerprint,
                        $"commit {commit} is not signed by any currently owner-role member's own chain"));
                    continue;
                }

                // The one-time backfill (h9k project assign-key) for a ledger whose genesis
                // predates this piece: a later, authorized rewrite of the genesis fingerprint's own
                // file that adds project_key without touching role, issued_at, or root_fingerprint.
                // Deliberately narrower than the general "authorized" check just above: signed
                // specifically by the genesis root's own chain, never merely any current owner-role
                // member's, so an owner who is not the genesis root can never mint or move this
                // project's own key, only the one identity the ledger's own genesis fact names.
                if (content is not null && projectKey is null && genesisRootFingerprint is { } genesisFingerprint
                    && fingerprint == genesisFingerprint
                    && ownerChains.TryGetValue(genesisFingerprint, out TrustedOwner? genesisChain)
                    && await IsAuthorizedByOwnerChainAsync(repositoryPath, commit, genesisChain, cancellationToken))
                {
                    projectKey = ExtractQuotedYamlValue(content, "project_key");
                }

                if (isDeletion)
                {
                    current.Remove(fingerprint);
                }
                else if (ParseRole(content) is { } role)
                {
                    current[fingerprint] = new ProjectMember(fingerprint, role, ParseIssuedAt(content));
                }
                else
                {
                    // A role that is neither "owner" nor "member" is malformed ledger data, not a
                    // valid closed-set value to guess at — recorded as unverifiable rather than
                    // silently granted Member-level trust (independent review finding: this
                    // previously coerced any unrecognized role, including a missing or garbled
                    // one, straight to Member).
                    unverified.Add(new UnverifiedLedgerWrite(
                        "membership", fingerprint, fingerprint,
                        $"commit {commit} declares a role that is neither \"owner\" nor \"member\""));
                }
            }
        }

        return ([.. current.Values], unverified, genesisRootFingerprint, projectKey);
    }

    /// <summary>
    /// Whether <paramref name="commit"/>, a members-ref write, is authorized by <paramref name="owner"/>'s
    /// own chain, as that chain stands right now: signed by the root's own key, or by any node
    /// <paramref name="owner"/> currently has enrolled. One rule, unconditionally, for every write
    /// regardless of when it landed or whether this is that signer's first accepted write or its
    /// hundredth — never a members-ref commit's own claimed committer date, which is a field its
    /// writer freely chooses and so is never a trustworthy anchor for anything (independent pre-PR
    /// review, cycle 1; ruled by the window, 2026-09-13). <see cref="ComputeMembersAsync"/>'s own doc
    /// names the accepted consequence: a revoked node's earlier, legitimately signed writes stop
    /// being authorized the moment it is revoked, and a later re-vouch restores them again.
    /// <para>
    /// The rule itself now lives in <see cref="OwnerChainAuthorization.IsAuthorizedByOwnerAsync"/>
    /// (idea 6be68ee2, trust-ledger finding 6) so a second caller outside this class —
    /// <c>PromptAddendaSweepEngine.MaterializeAsync</c>, walking a different ledger ref with a
    /// raw-commit-bytes signature check rather than this class' own sha-based one — applies the
    /// identical test without duplicating it.
    /// </para>
    /// </summary>
    private Task<bool> IsAuthorizedByOwnerChainAsync(
        string repositoryPath, string commit, TrustedOwner owner, CancellationToken cancellationToken) =>
        OwnerChainAuthorization.IsAuthorizedByOwnerAsync(
            owner, (key, token) => IsSignedByAsync(repositoryPath, commit, key, token), cancellationToken);

    /// <summary>
    /// Genesis's own authorization check, never used for any later membership write: identical to
    /// <see cref="IsAuthorizedByOwnerChainAsync"/>'s root-key check, but accepts a signature from
    /// EVERY node <paramref name="owner"/>'s chain has ever enrolled
    /// (<see cref="TrustedOwner.EverEnrolledNodes"/>), current or since revoked — never merely the
    /// ones live right now. Genesis is a one-time, immutable fact about who established this
    /// project (idea 202383dc, M2): unlike an ordinary write, there is no later re-vouch that could
    /// ever restore a lost genesis, so the accepted "revocation voids this signer's earlier writes"
    /// consequence <see cref="ComputeMembersAsync"/>'s own doc names for ordinary writes must never
    /// reach genesis itself.
    /// </summary>
    private async Task<bool> IsAuthorizedForGenesisAsync(
        string repositoryPath, string commit, TrustedOwner owner, CancellationToken cancellationToken)
    {
        foreach (LiveRootKey rootKey in owner.RootKeys)
        {
            if (await IsSignedByAsync(repositoryPath, commit, rootKey.PublicKeyLine, cancellationToken))
            {
                return true;
            }
        }

        foreach (TrustedNode node in owner.EverEnrolledNodes)
        {
            if (await IsSignedByAsync(repositoryPath, commit, node.PublicKeyLine, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFingerprint(string publicKeyLine, out string fingerprint)
    {
        try
        {
            fingerprint = NodeKeyStore.Fingerprint(publicKeyLine);
            return true;
        }
        catch (DomainValidationException)
        {
            fingerprint = string.Empty;
            return false;
        }
    }

    private static DateTimeOffset ParseIssuedAt(string? content)
    {
        string? raw = content is null ? null : ExtractQuotedYamlValue(content, "issued_at");
        return raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }

    /// <summary>Null for anything other than exactly "owner" or "member" — a missing or garbled
    /// role is refused rather than guessed at, since <see cref="MembershipRole"/> is a closed pair
    /// and every other value is invalid ledger data, not a third option to default to.</summary>
    private static MembershipRole? ParseRole(string? content)
    {
        string? raw = content is null ? null : ExtractQuotedYamlValue(content, "role");
        return raw switch
        {
            _ when string.Equals(raw, "owner", StringComparison.OrdinalIgnoreCase) => MembershipRole.Owner,
            _ when string.Equals(raw, "member", StringComparison.OrdinalIgnoreCase) => MembershipRole.Member,
            _ => null,
        };
    }

    /// <summary>
    /// The one place this class fetches a single append-only ref, through
    /// <see cref="LedgerAppendOnlyRefFetcher"/> — a private staging name, checked against the last
    /// verified tip before either the live or the verified ref ever moves (idea 6be68ee2, trust
    /// finding 8), rather than the plain <c>+refName:refName</c> fetch this method used to run
    /// directly against the shared local ref (and, on a confirmed-missing remote ref, used to delete
    /// outright — the bug this task's own review named: a ref origin has since deleted must be kept
    /// and read locally, never dropped). A genuine fetch failure is thrown rather than swallowed into
    /// "proceed as though this ref were simply absent": that would let an unreachable ledger compute
    /// as an empty or partial chain instead of surfacing the read as having failed at all
    /// (independent pre-PR review, cycle 1, adversarial lens, medium). A refusal (a rewind, a side
    /// merge, or a confirmed-gone remote ref this node still holds a verified tip for) never throws:
    /// <see cref="LedgerAppendOnlyFetchResult.WasRefused"/> and
    /// <see cref="LedgerAppendOnlyFetchResult.RefusalReason"/> are what each of this class' own three
    /// call sites turns into its own contextual <see cref="UnverifiedLedgerWrite"/>.
    /// </summary>
    private Task<LedgerAppendOnlyFetchResult> FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken) =>
        LedgerAppendOnlyRefFetcher.FetchAsync(runner, repositoryPath, refName, cancellationToken);

    /// <summary>Every commit reachable from <paramref name="tip"/>, oldest first —
    /// <c>--topo-order --first-parent</c> so replay follows the actual mainline commit graph rather
    /// than committer-date order (freely chosen by whoever signs a commit, and never consulted
    /// anywhere in this walk) and never descends into a merge's second parent at all, closing the
    /// path a backdated, merged-in commit would otherwise use to reorder itself earlier in ref
    /// history (independent pre-PR review, cycle 1, adversarial lens, medium).</summary>
    private async Task<IReadOnlyList<string>> CommitsOldestFirstAsync(
        string repositoryPath, string tip, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            repositoryPath, ["log", "--format=%H", "--reverse", "--topo-order", "--first-parent", tip], cancellationToken);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every commit reachable from <paramref name="tip"/> (mainline only —
    /// <c>--topo-order --first-parent</c>, the identical reasoning <see cref="CommitsOldestFirstAsync"/>
    /// applies) that touched <paramref name="path"/>, newest first — <c>[0]</c> is therefore the
    /// commit that currently produces whatever content a caller just read at <paramref name="tip"/>.</summary>
    private async Task<IReadOnlyList<string>> CommitsTouchingPathAsync(
        string repositoryPath, string tip, string path, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            repositoryPath, ["log", "--format=%H", "--topo-order", "--first-parent", tip, "--", path], cancellationToken);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every path <paramref name="commit"/> added, changed, or removed relative to its own
    /// mainline parent — <c>--root</c> so a commit with no parent (a fresh ref's first commit) diffs
    /// against the empty tree instead of failing, and <c>--diff-merges=first-parent</c> so a merge
    /// commit diffs against its first parent alone rather than git's own bare default of naming no
    /// paths at all for a merge (confirmed live against a throwaway repo). Every walk that feeds a
    /// commit here already replays <c>--topo-order --first-parent</c>
    /// (<see cref="CommitsOldestFirstAsync"/>), so a merge commit only ever appears when its first
    /// parent is the ref's own prior mainline tip — a path introduced purely via that merge's second
    /// parent must diff as mainline-introduced right here, or it is applied to nothing, recorded as
    /// unverified for nothing, and simply vanishes from the walk (independent pre-PR review,
    /// cycle 2, conformance lens, medium).</summary>
    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string repositoryPath, string commit, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            repositoryPath,
            ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "--diff-merges=first-parent", commit],
            cancellationToken);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>The content of <paramref name="path"/> at <paramref name="commit"/>'s own tree, or
    /// null when the path is not there — either it never existed at this commit, or (a members
    /// removal) this commit is the one that deleted it. Only that specific, documented git message
    /// is read as absence; any other failure (a corrupt or incomplete local object, disk I/O) is
    /// thrown instead of silently read as a deletion, which would let a broken local read change
    /// what this walk trusts rather than simply fail (independent review finding: this previously
    /// folded every non-zero exit from <c>git show</c> into absence).</summary>
    private async Task<string?> ReadAtCommitAsync(string repositoryPath, string commit, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["show", $"{commit}:{path}"], repositoryPath, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        if (result.StandardError.Contains("does not exist in", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new InvalidOperationException(
            $"git show {commit}:{path} failed in {repositoryPath} (exit {result.ExitCode}): "
            + $"{result.StandardError.Trim()} — refusing to read this as a deletion when the failure was "
            + "never confirmed to mean the path is actually absent.");
    }

    /// <summary>Verifies <paramref name="commitSha"/> was actually signed by
    /// <paramref name="publicKeyLine"/> — the identical technique
    /// <c>GitLedgerMessageTransport.IsSignedByRegisteredKeyAsync</c> uses (confirming the
    /// <c>gpgsig</c> header itself names an SSH signature before ever trusting
    /// <c>git verify-commit</c>'s own exit code, and writing only the fixed
    /// <see cref="AllowedSignersPrincipal"/> into the temporary <c>allowed_signers</c> file — never
    /// the commit's own committer email, which the commit's own author controls), duplicated rather
    /// than shared: a different class in a different concern, and this narrow, already-proven shape
    /// is cheaper to repeat than to widen a seam neither caller needs generalized (the same call
    /// <c>GitLedgerMessageTransport</c> itself makes about its own private process runner).</summary>
    private async Task<bool> IsSignedByAsync(string repositoryPath, string commitSha, string publicKeyLine, CancellationToken cancellationToken)
    {
        string? rawCommit = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", commitSha], cancellationToken);
        if (rawCommit is null || !HasSshSignatureHeader(rawCommit))
        {
            return false;
        }

        string allowedSignersFile = Path.Combine(Path.GetTempPath(), $"h9k-chain-allowed-signers-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(allowedSignersFile, $"{AllowedSignersPrincipal} {publicKeyLine}\n", cancellationToken);
            ProcessResult result = await runner(
                "git",
                ["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={allowedSignersFile}", "verify-commit", commitSha],
                repositoryPath,
                cancellationToken);
            return result.ExitCode == 0;
        }
        finally
        {
            try
            {
                File.Delete(allowedSignersFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file; nothing downstream reads it again.
            }
        }
    }

    internal static bool HasSshSignatureHeader(string rawCommitObject)
    {
        const string header = "gpgsig ";
        const string sshMarker = "-----BEGIN SSH SIGNATURE-----";
        foreach (string rawLine in rawCommitObject.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith(header, StringComparison.Ordinal))
            {
                return line[header.Length..].StartsWith(sshMarker, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>Reverses the small, flat, every-value-double-quoted YAML shape every ledger file in
    /// this task uses — the same reader <c>GitLedgerMessageTransport.ExtractQuotedYamlValue</c>
    /// already is for <c>node.yaml</c>, duplicated for the same reason <see cref="IsSignedByAsync"/> is.</summary>
    internal static string? ExtractQuotedYamlValue(string yaml, string key)
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

    private async Task<string?> RunGitCaptureAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    /// <summary>For a walk that has already confirmed <c>tip</c> or <c>commit</c> resolves: a log
    /// or diff-tree over a commit this walk already knows exists has no legitimate failure mode, so
    /// unlike <see cref="RunGitCaptureAsync"/> a non-zero exit here is thrown rather than folded
    /// into "no commits"/"no changed paths" — a corrupt pack or a local git failure must stop the
    /// walk, never silently read as though that commit changed nothing at all (independent review
    /// finding, the same fail-closed reasoning <see cref="ReadAtCommitAsync"/> now applies).</summary>
    private async Task<string> RunGitCaptureOrThrowAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in {repositoryPath} (exit {result.ExitCode}): "
                + $"{result.StandardError.Trim()} — refusing to read this commit as though it changed nothing.");
        }

        return result.StandardOutput;
    }
}
