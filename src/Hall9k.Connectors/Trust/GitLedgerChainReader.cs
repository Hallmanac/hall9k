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
/// members file is genesis (self-written, unconditional), every later one needs a signer already
/// recognized as an Owner-role member's own key <em>at the time that members-ref commit itself
/// landed</em> — never against an owner chain's own final state, which would let a later
/// revocation retroactively void an earlier, legitimately signed membership write (independent
/// pre-PR review, cycle 1, conformance and adversarial lenses, medium and high). A write whose
/// signer cannot be verified this way is silently ignored — never a thrown exception — so a
/// stranger's self-consistent root and node files simply never enter
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

        Dictionary<string, OwnerChainTimeline> timelines = [];
        List<UnverifiedLedgerWrite> unverified = [];
        foreach (string root in roots)
        {
            (OwnerChainTimeline? timeline, IReadOnlyList<UnverifiedLedgerWrite> rootUnverified) =
                await ComputeOwnerChainAsync(repositoryPath, root, cancellationToken);
            unverified.AddRange(rootUnverified);
            if (timeline is not null)
            {
                timelines[root] = timeline;
                unverified.AddRange(timeline.Unverified);
            }
        }

        Dictionary<string, TrustedOwner> ownerChains = timelines.ToDictionary(pair => pair.Key, pair => pair.Value.Final);

        (IReadOnlyList<ProjectMember> members, IReadOnlyList<UnverifiedLedgerWrite> memberUnverified) =
            await ComputeMembersAsync(repositoryPath, timelines, cancellationToken);
        unverified.AddRange(memberUnverified);

        return new TrustChain(ownerChains, members, unverified);
    }

    /// <summary>Every <c>refs/hall9k/ledger/owners/&lt;fingerprint&gt;</c> ref origin currently
    /// holds, from one <c>ls-remote</c> against the whole prefix — mirrors
    /// <c>GitLedgerMessageTransport.ProbeCoreAsync</c>'s own parsing exactly, including its own
    /// choice to throw on a genuine failure rather than read one as "no roots at all" (independent
    /// pre-PR review, cycle 1, adversarial lens, medium).</summary>
    private async Task<IReadOnlyList<string>> DiscoverOwnerRootsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["ls-remote", "origin", $"{OwnersRefPrefix}*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-remote against {repositoryPath} for the owners prefix failed "
                + $"(exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        List<string> roots = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string refName = parts[1].Trim();
            if (refName.StartsWith(OwnersRefPrefix, StringComparison.Ordinal))
            {
                roots.Add(refName[OwnersRefPrefix.Length..]);
            }
        }

        return roots;
    }

    /// <summary>One owner-ref commit's own effect on the chain being built: a vouch adds
    /// <see cref="Node"/> under <see cref="NodeId"/>, a revocation removes it — <see cref="Time"/>
    /// is that commit's own committer timestamp, what <see cref="OwnerChainTimeline.AsOf"/> folds
    /// against to answer "what did this chain look like at some other, earlier point".</summary>
    private sealed record OwnerChainEvent(DateTimeOffset Time, string NodeId, bool IsRevoke, TrustedNode? Node);

    /// <summary>
    /// One root's own chain, kept as a timeline rather than only its final state: every vouch and
    /// revocation this walk accepted, in ref order, each carrying the committer timestamp of the
    /// commit that made it. <see cref="Final"/> is the ordinary "what does this chain look like
    /// right now" reading; <see cref="AsOf"/> is what <see cref="ComputeMembersAsync"/> uses
    /// instead, so a members-ref write's own signer is checked against the owner chain as it stood
    /// when that write itself landed, not against whatever the chain has become since (a later
    /// revocation must never retroactively void an earlier, legitimately signed write — independent
    /// pre-PR review, cycle 1, conformance and adversarial lenses, medium and high).
    /// </summary>
    private sealed record OwnerChainTimeline(
        string RootFingerprint,
        string RootPublicKeyLine,
        IReadOnlyList<OwnerChainEvent> Events,
        IReadOnlyList<UnverifiedLedgerWrite> Unverified)
    {
        public TrustedOwner Final => AsOf(DateTimeOffset.MaxValue);

        public TrustedOwner AsOf(DateTimeOffset time)
        {
            Dictionary<string, TrustedNode> nodes = [];
            foreach (OwnerChainEvent change in Events)
            {
                if (change.Time > time)
                {
                    continue;
                }

                if (change.IsRevoke)
                {
                    nodes.Remove(change.NodeId);
                }
                else
                {
                    nodes[change.NodeId] = change.Node!;
                }
            }

            return new TrustedOwner(RootFingerprint, RootPublicKeyLine, [.. nodes.Values]);
        }
    }

    /// <summary>
    /// One root's own chain: self-certification (the ref name, the declared public key's own
    /// fingerprint, and the commit that currently produces that content must all agree), then every
    /// vouch or revocation in the ref's own history, oldest first, each one accepted only when its
    /// signer is already a member of the chain being built at that point — the root's own key from
    /// the start, and every node this same walk has already vouched in. Returns a null timeline
    /// when the root cannot be self-certified at all: nothing about it is trusted, root key
    /// included — paired with an unverified-write diagnostic naming the specific offending commit
    /// whenever one exists to name, rather than the whole chain silently vanishing with no
    /// diagnostic at all and, for the key-mismatch case specifically, the genesis branch elsewhere
    /// misreporting the honest genesis writer as the cause (independent pre-PR review, cycle 1,
    /// conformance lens, medium).
    /// </summary>
    private async Task<(OwnerChainTimeline? Timeline, IReadOnlyList<UnverifiedLedgerWrite> Unverified)> ComputeOwnerChainAsync(
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

        if (rootCommits.Count == 0 || !await IsSignedByAsync(repositoryPath, rootCommits[0], publicKeyLine, cancellationToken))
        {
            return (null, [new UnverifiedLedgerWrite(
                "root", root, root, $"commit {culpritCommit} for {rootPath} is not signed by that root's own key")]);
        }

        Dictionary<string, TrustedNode> nodes = [];
        List<OwnerChainEvent> events = [];
        List<UnverifiedLedgerWrite> unverified = [];
        string nodesPrefix = $"owners/{root}/nodes/";
        string revokedPrefix = $"owners/{root}/revoked/";

        IReadOnlyList<(string Sha, DateTimeOffset Time)> allCommits = await CommitsOldestFirstAsync(repositoryPath, tip, cancellationToken);
        foreach ((string commit, DateTimeOffset commitTime) in allCommits)
        {
            foreach (string path in await ChangedPathsAsync(repositoryPath, commit, cancellationToken))
            {
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
                    events.Add(new OwnerChainEvent(commitTime, nodeId, IsRevoke: true, Node: null));
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
                TrustedNode node = new(nodeId, nodePublicKey, nodeFingerprint, issuedAt);
                nodes[nodeId] = node;
                events.Add(new OwnerChainEvent(commitTime, nodeId, IsRevoke: false, Node: node));
            }
        }

        return (new OwnerChainTimeline(root, publicKeyLine, events, unverified), []);
    }

    /// <summary>
    /// Replays <c>refs/hall9k/ledger/members</c> oldest to newest. The very first commit the ref's
    /// own history ever holds that touches a <c>members/*.yaml</c> path is genesis: accepted only
    /// when self-written (the writer's key traces to the very root fingerprint the file names),
    /// unconditionally the project's first owner-role member either way — win or lose, that slot is
    /// spent once. Every later write needs its signer to already belong to a currently Owner-role
    /// member's own chain.
    /// <para>
    /// <em>Which point in that chain "already belong" is checked against</em>, for a signer that is
    /// a vouched node rather than the root key itself, depends on whether this is the first
    /// authorized (non-genesis) write this walk has accepted from that specific node's own key,
    /// tracked as we go by <see cref="IsAuthorizedByOwnerChainAsync"/> — never the whole owner
    /// chain, and never the root key, which self-certifies once and is never subject to revocation
    /// the way a vouched node is: the first write from a given node's key is checked against the
    /// chain <em>as it stood when this commit itself landed</em> (<see cref="OwnerChainTimeline.AsOf"/>,
    /// keyed by this commit's own claimed committer date) — never the chain's own final state,
    /// which would let a node revoked after the fact retroactively void a write it made while still
    /// legitimately enrolled (independent pre-PR review, cycle 1, conformance and adversarial
    /// lenses, medium and high). Every write after that node's first one is checked against the
    /// chain's own current, live state instead (<see cref="OwnerChainTimeline.Final"/>), never
    /// <c>AsOf</c> again: a members-ref commit's own committer date is a field its writer freely
    /// chooses, so trusting it lets a revoked node keep re-authorizing itself indefinitely by
    /// backdating a fresh commit to precede its own real revocation (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, both high) — closed for every write after that
    /// node's first, real one, which nothing forges. The first write from a node that has never yet
    /// written to the members ref keeps trusting its own claimed date, because nothing else
    /// non-forgeable is available yet to bound it against; fully closing that narrower, single-use
    /// race needs a trusted anchor this reader does not have (a persisted, writer-independent
    /// observation time, or a witnessed owner-chain commitment made at write time), not a change to
    /// this replay — flagged in this task's own PR summary for human judgment on the residual risk.
    /// </para>
    /// Anything unauthorized is ignored, including a later self-claimed owner with no vouch (idea
    /// 202383dc, T1 criterion 2) — and recorded in the returned unverified list rather than silently
    /// dropped.
    /// </summary>
    private async Task<(IReadOnlyList<ProjectMember> Members, IReadOnlyList<UnverifiedLedgerWrite> Unverified)> ComputeMembersAsync(
        string repositoryPath, IReadOnlyDictionary<string, OwnerChainTimeline> timelines, CancellationToken cancellationToken)
    {
        await FetchRefAsync(repositoryPath, MembersRefName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, MembersRefName, cancellationToken);
        if (tip is null)
        {
            return ([], []);
        }

        const string prefix = "members/";
        const string suffix = ".yaml";
        Dictionary<string, ProjectMember> current = [];
        List<UnverifiedLedgerWrite> unverified = [];

        // Every vouched-node fingerprint that has already had one members-ref write accepted
        // through this walk, scoped to the specific key — never the whole owner chain, which a
        // legitimate root key or a second, still-enrolled node under the same owner would wrongly
        // penalize for a first, unrelated node's own earlier write (IsAuthorizedByOwnerChainAsync's
        // own doc explains why).
        HashSet<string> nodesWithAuthorizedWrite = [];
        bool genesisDecided = false;

        foreach ((string commit, DateTimeOffset commitTime) in await CommitsOldestFirstAsync(repositoryPath, tip, cancellationToken))
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
                    if (!isDeletion
                        && timelines.TryGetValue(fingerprint, out OwnerChainTimeline? selfOwner)
                        && await IsSignedByAsync(repositoryPath, commit, selfOwner.RootPublicKeyLine, cancellationToken))
                    {
                        current[fingerprint] = new ProjectMember(fingerprint, MembershipRole.Owner, ParseIssuedAt(content));
                    }
                    else if (!isDeletion)
                    {
                        unverified.Add(new UnverifiedLedgerWrite(
                            "membership", fingerprint, fingerprint,
                            $"genesis commit {commit} for {fingerprint} is not self-signed by that root's own key"));
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

                    if (!timelines.TryGetValue(member.RootFingerprint, out OwnerChainTimeline? owner))
                    {
                        continue;
                    }

                    if (await IsAuthorizedByOwnerChainAsync(
                        repositoryPath, commit, owner, commitTime, nodesWithAuthorizedWrite, cancellationToken))
                    {
                        authorized = true;
                        break;
                    }
                }

                if (!authorized)
                {
                    unverified.Add(new UnverifiedLedgerWrite(
                        "membership", fingerprint, fingerprint,
                        $"commit {commit} is not signed by any currently owner-role member's own chain, "
                        + "as that chain stood when this commit landed"));
                    continue;
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

        return ([.. current.Values], unverified);
    }

    /// <summary>
    /// Whether <paramref name="commit"/>, a members-ref write, is authorized by <paramref name="owner"/>'s
    /// own chain — the root's own key always qualifies, unconditionally, since it self-certifies
    /// once and is never itself subject to revocation the way a vouched node is. A vouched node's
    /// key is judged against <see cref="OwnerChainTimeline.AsOf"/>, pinned to <paramref name="commitTime"/>
    /// (this write's own claimed committer date, freely chosen by whoever signs it), only the first
    /// time this walk has ever accepted a write from that specific node's own key — recorded into
    /// <paramref name="nodesWithAuthorizedWrite"/> the moment it does. Every write after that node's
    /// first one is judged against the chain's own current, live state (<see cref="OwnerChainTimeline.Final"/>)
    /// instead, never <paramref name="commitTime"/> again: trusting a freely-chosen date more than
    /// once per node is what let a revoked node keep re-authorizing itself indefinitely by
    /// backdating a fresh commit ahead of its own real revocation (independent pre-PR review, cycle
    /// 1, conformance and adversarial lenses, both high) — <see cref="ComputeMembersAsync"/>'s own
    /// doc discloses the residual, single-use gap this narrows the exploit down to, rather than
    /// closes outright.
    /// </summary>
    private async Task<bool> IsAuthorizedByOwnerChainAsync(
        string repositoryPath, string commit, OwnerChainTimeline owner, DateTimeOffset commitTime,
        HashSet<string> nodesWithAuthorizedWrite, CancellationToken cancellationToken)
    {
        if (await IsSignedByAsync(repositoryPath, commit, owner.RootPublicKeyLine, cancellationToken))
        {
            return true;
        }

        TrustedOwner finalOwner = owner.Final;
        TrustedOwner asOfOwner = owner.AsOf(commitTime);
        Dictionary<string, TrustedNode> everSeenNodes = [];
        foreach (TrustedNode node in finalOwner.Nodes)
        {
            everSeenNodes[node.Fingerprint] = node;
        }

        foreach (TrustedNode node in asOfOwner.Nodes)
        {
            everSeenNodes.TryAdd(node.Fingerprint, node);
        }

        foreach (TrustedNode node in everSeenNodes.Values)
        {
            if (!await IsSignedByAsync(repositoryPath, commit, node.PublicKeyLine, cancellationToken))
            {
                continue;
            }

            bool enrolledNow = finalOwner.Nodes.Any(candidate => candidate.Fingerprint == node.Fingerprint);
            bool enrolledThen = asOfOwner.Nodes.Any(candidate => candidate.Fingerprint == node.Fingerprint);
            bool authorizedHere = nodesWithAuthorizedWrite.Contains(node.Fingerprint) ? enrolledNow : enrolledThen;
            if (authorizedHere)
            {
                nodesWithAuthorizedWrite.Add(node.Fingerprint);
            }

            // The signing key was found — no other node under this owner could also match the
            // identical signature, so there is nothing left to gain by checking the rest.
            return authorizedHere;
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

    private async Task<string?> ResolveTipAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        return tip.IsBlank() ? null : tip;
    }

    /// <summary>Every commit reachable from <paramref name="tip"/>, oldest first, paired with its
    /// own committer timestamp — <c>--topo-order --first-parent</c> so replay follows the actual
    /// mainline commit graph rather than committer-date order (freely chosen by whoever signs a
    /// commit) and never descends into a merge's second parent at all, closing the path a
    /// backdated, merged-in commit would otherwise use to reorder itself earlier in ref history
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).</summary>
    private async Task<IReadOnlyList<(string Sha, DateTimeOffset Time)>> CommitsOldestFirstAsync(
        string repositoryPath, string tip, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            repositoryPath, ["log", "--format=%H%x09%cI", "--reverse", "--topo-order", "--first-parent", tip], cancellationToken);

        List<(string, DateTimeOffset)> commits = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2
                || !DateTimeOffset.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time))
            {
                continue;
            }

            commits.Add((parts[0].Trim(), time));
        }

        return commits;
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
