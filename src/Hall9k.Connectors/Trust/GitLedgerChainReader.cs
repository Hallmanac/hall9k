using System.Globalization;
using Hall9k.Connectors.Identity;
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

        unverified.AddRange(await AttachRootNodeIdsAsync(repositoryPath, ownerChains, cancellationToken));

        (IReadOnlyList<ProjectMember> members, IReadOnlyList<UnverifiedLedgerWrite> memberUnverified,
            string? genesisRootFingerprint, string? projectKey) =
            await ComputeMembersAsync(repositoryPath, ownerChains, cancellationToken);
        unverified.AddRange(memberUnverified);

        return new TrustChain(ownerChains, members, unverified, genesisRootFingerprint, projectKey);
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

        List<string> suffixes = [];
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

        return suffixes;
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
    /// the retired root still self-certifies above — and the rerun's own <c>WriteNodeFileAsync</c>
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
    private async Task<IReadOnlyList<UnverifiedLedgerWrite>> AttachRootNodeIdsAsync(
        string repositoryPath, Dictionary<string, TrustedOwner> ownerChains, CancellationToken cancellationToken)
    {
        if (ownerChains.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> nodeIds = await DiscoverNodeIdsAsync(repositoryPath, cancellationToken);
        if (nodeIds.Count == 0)
        {
            return [];
        }

        await FetchRefsAsync(repositoryPath, NodesRefPrefix, cancellationToken);

        List<UnverifiedLedgerWrite> unverified = [];
        foreach (string nodeId in nodeIds)
        {
            string refName = $"{NodesRefPrefix}{nodeId}";
            string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
            if (tip is null)
            {
                continue;
            }

            string path = $"nodes/{nodeId}/node.yaml";
            string? content = await ReadAtCommitAsync(repositoryPath, tip, path, cancellationToken);
            string? publicKeyLine = content is null ? null : ExtractQuotedYamlValue(content, "public_key");
            string? ownerFingerprintLine = content is null ? null : ExtractQuotedYamlValue(content, "owner_fingerprint");
            if (publicKeyLine is null || !TryFingerprint(publicKeyLine, out string fingerprint)
                || !ownerChains.TryGetValue(fingerprint, out TrustedOwner? owner))
            {
                // No key, an unparseable one, or a fingerprint that names no root this walk trusts.
                continue;
            }

            IReadOnlyList<string> commits = await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken);
            if (commits.Count > 0 && await IsSignedByAsync(repositoryPath, commits[0], owner.RootPublicKeyLine, cancellationToken))
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

        return unverified;
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
        await FetchRefAsync(repositoryPath, refName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        if (tip is null)
        {
            // No ref, nothing pushed under this root's own namespace yet — there is no commit here
            // to name as an offending write.
            return (null, []);
        }

        string rootPath = $"owners/{root}/root.yaml";
        string? rootContent = await ReadAtCommitAsync(repositoryPath, tip, rootPath, cancellationToken);
        if (rootContent is null)
        {
            // The ref exists (the namespace was at least touched) but root.yaml itself never landed
            // or was deleted — genuinely nothing here yet, not a write that failed verification.
            return (null, []);
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
            return (null, [new UnverifiedLedgerWrite(
                "root", root, root,
                $"commit {culpritCommit} for {rootPath} does not self-certify: its own declared public key "
                + "does not fingerprint back to this root")]);
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
        foreach (string commit in allCommits)
        {
            foreach (string path in await ChangedPathsAsync(repositoryPath, commit, cancellationToken))
            {
                if (path.StartsWith(carriedPrefix, StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    string carriedNodeId = path[carriedPrefix.Length..^".yaml".Length];
                    if (carriedNodeId.IsBlank())
                    {
                        continue;
                    }

                    // Gated on a PRIOR SUCCESSFUL establishment of this exact node id, never merely
                    // a prior touch of the path: an earlier commit that failed verification (a
                    // stranger's garbage, or this same carrying node's own malformed retry) must
                    // never block a later, genuinely valid carry for the identical node id from
                    // still self-authorizing on its own embedded evidence — nothing was ever
                    // actually established for this node yet, so there is nothing to require
                    // re-authorization from (independent review finding, round two: the first draft
                    // of this fix gated on "already touched," which let one bad early commit at a
                    // node id's own path permanently lock out every later, legitimate one).
                    if (carriedPathsEstablished.Contains(carriedNodeId))
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
                                $"commit {commit} for {path} rewrites an already-established carried record but is not "
                                + $"signed by root {root} or any node currently enrolled in it"));
                            continue;
                        }
                    }

                    (TrustedNode? carriedNode, UnverifiedLedgerWrite? failure) = await VerifyCarriedRecordAsync(
                        repositoryPath, commit, path, carriedNodeId, root, publicKeyLine, cancellationToken);
                    if (failure is not null)
                    {
                        unverified.Add(failure);
                        continue;
                    }

                    if (carriedNode is not null)
                    {
                        nodes[carriedNodeId] = carriedNode;
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
                nodes[nodeId] = new TrustedNode(nodeId, nodePublicKey, nodeFingerprint, issuedAt);
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
            return (null, [new UnverifiedLedgerWrite(
                "root", root, root,
                $"commit {culpritCommit} for {rootPath} is not signed by that root's own key, and no "
                + $"owners/{root}/carried/*.yaml bundle verifies it either"), .. unverified]);
        }

        return (new TrustedOwner(root, publicKeyLine, [.. nodes.Values], RevokedNodeIds: revokedNodeIds), unverified);
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
    /// this bundle carries is trusted yet at that point;</item>
    /// <item>the carried node's own declared public key equals THIS ledger's own current
    /// <c>nodes/&lt;node-id&gt;/node.yaml</c>, and that file's own commit is self-signed by that
    /// same key — the binding that ties this bundle to whoever is actually running the join on
    /// <em>this</em> machine: only the holder of that node's own private key can have produced a
    /// self-signed <c>node.yaml</c> declaring it here (idea's own origin note: "the bundle binds to
    /// the carrying node because only the holder of that node private key can self-announce with
    /// the same public key on B").
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
    private async Task<(TrustedNode? Node, UnverifiedLedgerWrite? Failure)> VerifyCarriedRecordAsync(
        string repositoryPath, string commit, string path, string nodeId, string root, string rootPublicKeyLine,
        CancellationToken cancellationToken)
    {
        string? content = await ReadAtCommitAsync(repositoryPath, commit, path, cancellationToken);
        if (content is null)
        {
            // A deletion (or, on the pre-check's own replay, simply not the content this commit
            // happens to hold) — nothing to verify or complain about.
            return (null, null);
        }

        string? nodePublicKey = ExtractQuotedYamlValue(content, "node_public_key");
        string? rootYamlBase64 = ExtractQuotedYamlValue(content, "root_yaml_base64");
        string? rootCommitBase64 = ExtractQuotedYamlValue(content, "root_commit_base64");
        string? vouchCommitBase64 = ExtractQuotedYamlValue(content, "vouch_commit_base64");
        if (nodePublicKey is null || rootYamlBase64 is null || rootCommitBase64 is null || vouchCommitBase64 is null)
        {
            return (null, Unverified("is missing a required field"));
        }

        // Check 1: the embedded root.yaml's own declared public key fingerprints to `root`.
        if (!TryDecodeBase64(rootYamlBase64, out string embeddedRootYaml))
        {
            return (null, Unverified("carries an embedded root.yaml that is not valid base64"));
        }

        string? embeddedRootPublicKey = ExtractQuotedYamlValue(embeddedRootYaml, "public_key");
        if (embeddedRootPublicKey is null || !TryFingerprint(embeddedRootPublicKey, out string embeddedFingerprint)
            || embeddedFingerprint != root)
        {
            return (null, Unverified("carries an embedded root.yaml that does not self-certify to this root"));
        }

        // Check 2: the embedded root commit is SSH-signed by that key.
        if (!TryDecodeBase64(rootCommitBase64, out string rootCommitBytes)
            || !await IsSignedByRawBytesAsync(repositoryPath, rootCommitBytes, embeddedRootPublicKey, cancellationToken))
        {
            return (null, Unverified("carries an embedded root commit that is not signed by the root's own key"));
        }

        // Check 3: the embedded vouch commit is signed by the root key (single-hop, see the doc
        // above), AND its own raw bytes — the exact payload that signature covers, never something
        // this method trusts on faith — name this specific node id. Every vouch this platform ever
        // writes carries the node id in its own commit message (NodeVouchCommand: "Vouch node
        // {id}"; InviteSweepEngine: "Vouch node {id} (invite)"), so a genuine vouch commit for a
        // DIFFERENT node — or any other commit root ever happened to sign, root.yaml's own
        // establishing commit included — can never satisfy this. Without it, any commit root ever
        // signed for any reason at all (root.yaml's own commit is already embedded and proven
        // signed by check 2, and is trivially reusable here too) would satisfy "signed by root",
        // letting a bundle claim root vouched a node it never vouched at all (independent pre-PR
        // review, adversarial lens, critical).
        if (!TryDecodeBase64(vouchCommitBase64, out string vouchCommitBytes)
            || !await IsSignedByRawBytesAsync(repositoryPath, vouchCommitBytes, embeddedRootPublicKey, cancellationToken)
            || !vouchCommitBytes.Contains(nodeId, StringComparison.OrdinalIgnoreCase))
        {
            return (null, Unverified(
                "carries an embedded vouch commit that is not signed by the root's own key, or is signed but never names this node"));
        }

        // Check 4: the carried node's own declared public key equals this ledger's own current
        // node.yaml for the same node id, and that file's own commit is self-signed by that key.
        if (!TryFingerprint(nodePublicKey, out string nodeFingerprint))
        {
            return (null, Unverified("declares a node public key that is malformed"));
        }

        string nodeRefName = $"{NodesRefPrefix}{nodeId}";
        string nodePath = $"nodes/{nodeId}/node.yaml";
        await FetchRefAsync(repositoryPath, nodeRefName, cancellationToken);
        string? localTip = await ResolveTipAsync(repositoryPath, nodeRefName, cancellationToken);
        string? localNodeContent = localTip is null ? null : await ReadAtCommitAsync(repositoryPath, localTip, nodePath, cancellationToken);
        string? localPublicKey = localNodeContent is null ? null : ExtractQuotedYamlValue(localNodeContent, "public_key");
        if (localPublicKey is null || localPublicKey != nodePublicKey)
        {
            return (null, Unverified($"declares a node public key that does not match this ledger's own {nodePath}"));
        }

        IReadOnlyList<string> localNodeCommits = await CommitsTouchingPathAsync(repositoryPath, localTip!, nodePath, cancellationToken);
        if (localNodeCommits.Count == 0 || !await IsSignedByAsync(repositoryPath, localNodeCommits[0], localPublicKey, cancellationToken))
        {
            return (null, Unverified($"this ledger's own {nodePath} is not self-signed by the key it declares"));
        }

        return (new TrustedNode(nodeId, nodePublicKey, nodeFingerprint, ParseIssuedAt(content)), null);

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
    /// when self-written (the writer's key traces to the very root fingerprint the file names),
    /// unconditionally the project's first owner-role member either way — win or lose, that slot is
    /// spent once. Every later write needs its signer to belong, right now, to a currently
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
        await FetchRefAsync(repositoryPath, MembersRefName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, MembersRefName, cancellationToken);
        if (tip is null)
        {
            return ([], [], null, null);
        }

        const string prefix = "members/";
        const string suffix = ".yaml";
        Dictionary<string, ProjectMember> current = [];
        List<UnverifiedLedgerWrite> unverified = [];
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
                    // Signed by the root's own key OR by any node this same root's chain currently
                    // enrols — never merely "self-signed by the root's own key" alone (task f53fecfd,
                    // criterion 3): a carried record establishes root R with no local node ever
                    // holding R's own private key, so the genesis member commit for R can only ever
                    // be signed by the carried node itself. IsAuthorizedByOwnerChainAsync is the
                    // identical rule every later membership write already uses; genesis differs only
                    // in being unconditionally accepted once authorized, no existing owner-role
                    // member required first.
                    if (content is not null
                        && ownerChains.TryGetValue(fingerprint, out TrustedOwner? selfOwner)
                        && await IsAuthorizedByOwnerChainAsync(repositoryPath, commit, selfOwner, cancellationToken))
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
    /// </summary>
    private async Task<bool> IsAuthorizedByOwnerChainAsync(
        string repositoryPath, string commit, TrustedOwner owner, CancellationToken cancellationToken)
    {
        if (await IsSignedByAsync(repositoryPath, commit, owner.RootPublicKeyLine, cancellationToken))
        {
            return true;
        }

        foreach (TrustedNode node in owner.Nodes)
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

    /// <summary>Fetches <paramref name="refName"/> fresh. A missing remote ref is not a failure —
    /// git's exit code for it is indistinguishable from a genuine one, so the distinction is read
    /// from git's own message, the identical reasoning <c>GitLedgerMessageTransport</c>'s own
    /// <c>FetchRefAsync</c> applies. A genuine failure (network, credentials) is thrown rather than
    /// swallowed into "proceed as though this ref were simply absent": that would let an
    /// unreachable ledger compute as an empty or partial chain instead of surfacing the read as
    /// having failed at all (independent pre-PR review, cycle 1, adversarial lens, medium). A
    /// confirmed-missing remote ref also clears any local copy an earlier fetch left behind —
    /// otherwise <see cref="ResolveTipAsync"/> would keep reading that stale local tip as though
    /// origin still held it, letting a ref origin has since deleted go on granting whatever it
    /// last granted (independent review finding).</summary>
    private async Task FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["fetch", "origin", $"+{refName}:{refName}"], repositoryPath, cancellationToken);
        if (result.ExitCode == 0)
        {
            return;
        }

        if (!result.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"git fetch of {refName} from origin in {repositoryPath} failed (exit {result.ExitCode}): "
                + $"{result.StandardError.Trim()} — refusing to compute trust from a possibly stale or "
                + "incomplete read.");
        }

        // Best-effort: if no local copy exists either, this is simply a no-op.
        await runner("git", ["update-ref", "-d", refName], repositoryPath, cancellationToken);
    }

    /// <summary>Fetches every ref under <paramref name="prefix"/> in one round trip, by a single
    /// wildcard refspec — the batched counterpart to <see cref="FetchRefAsync"/>'s one-ref-at-a-time
    /// shape, for a caller (<see cref="AttachRootNodeIdsAsync"/>) that already knows, from its own
    /// prior <c>ls-remote</c>, every suffix under this prefix it is about to read. A wildcard
    /// refspec matching zero remote refs is not a failure the way a missing single named ref is
    /// (git simply fetches nothing), so unlike <see cref="FetchRefAsync"/> there is no
    /// "couldn't find remote ref" case to special-case here, and no local-ref cleanup: a caller
    /// that already enumerated the current suffixes from origin never reads a stale local ref for a
    /// suffix origin no longer lists, because it never asks for it by name.</summary>
    private async Task FetchRefsAsync(string repositoryPath, string prefix, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["fetch", "origin", $"+{prefix}*:{prefix}*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git fetch of {prefix}* from origin in {repositoryPath} failed (exit {result.ExitCode}): "
                + $"{result.StandardError.Trim()} — refusing to compute trust from a possibly stale or "
                + "incomplete read.");
        }
    }

    private async Task<string?> ResolveTipAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        return tip.IsBlank() ? null : tip;
    }

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
