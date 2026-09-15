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
/// members file is genesis (self-written, unconditional), every later one needs a currently
/// recognized Owner-role member's own signature. A write whose signer cannot be verified this way
/// is silently ignored — never a thrown exception — so a stranger's self-consistent root and node
/// files simply never enter <see cref="TrustChain.OwnerChains"/>'s membership-restricted view.
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

    public async Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = await DiscoverOwnerRootsAsync(repositoryPath, cancellationToken);

        Dictionary<string, TrustedOwner> ownerChains = [];
        foreach (string root in roots)
        {
            TrustedOwner? chain = await ComputeOwnerChainAsync(repositoryPath, root, cancellationToken);
            if (chain is not null)
            {
                ownerChains[root] = chain;
            }
        }

        IReadOnlyList<ProjectMember> members = await ComputeMembersAsync(repositoryPath, ownerChains, cancellationToken);
        return new TrustChain(ownerChains, members);
    }

    /// <summary>Every <c>refs/hall9k/ledger/owners/&lt;fingerprint&gt;</c> ref origin currently
    /// holds, from one <c>ls-remote</c> against the whole prefix — mirrors
    /// <c>GitLedgerMessageTransport.ProbeCoreAsync</c>'s own parsing exactly.</summary>
    private async Task<IReadOnlyList<string>> DiscoverOwnerRootsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["ls-remote", "origin", $"{OwnersRefPrefix}*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
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

    /// <summary>
    /// One root's own chain: self-certification (the ref name, the declared public key's own
    /// fingerprint, and the commit that introduced <c>root.yaml</c> must all agree), then every
    /// vouch or revocation in the ref's own history, oldest first, each one accepted only when its
    /// signer is already a member of the chain being built — the root's own key from the start,
    /// and every node this same walk has already vouched in. Returns null when the root cannot be
    /// self-certified at all: nothing about it is trusted, root key included.
    /// </summary>
    private async Task<TrustedOwner?> ComputeOwnerChainAsync(string repositoryPath, string root, CancellationToken cancellationToken)
    {
        string refName = $"{OwnersRefPrefix}{root}";
        if (!await FetchRefAsync(repositoryPath, refName, cancellationToken))
        {
            return null;
        }

        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        if (tip is null)
        {
            return null;
        }

        string rootPath = $"owners/{root}/root.yaml";
        string? rootContent = await ReadAtCommitAsync(repositoryPath, tip, rootPath, cancellationToken);
        if (rootContent is null)
        {
            return null;
        }

        string? publicKeyLine = ExtractQuotedYamlValue(rootContent, "public_key");
        if (publicKeyLine is null || !TryFingerprint(publicKeyLine, out string actualFingerprint) || actualFingerprint != root)
        {
            // Either malformed, or someone else's public key declared under this fingerprint's own
            // namespace — self-certification fails either way, so nothing here is trusted.
            return null;
        }

        IReadOnlyList<string> rootCommits = await CommitsTouchingPathAsync(repositoryPath, tip, rootPath, cancellationToken);
        if (rootCommits.Count == 0 || !await IsSignedByAsync(repositoryPath, rootCommits[^1], publicKeyLine, cancellationToken))
        {
            return null;
        }

        Dictionary<string, TrustedNode> nodes = [];
        string nodesPrefix = $"owners/{root}/nodes/";
        string revokedPrefix = $"owners/{root}/revoked/";

        IReadOnlyList<string> allCommits = await CommitsOldestFirstAsync(repositoryPath, tip, cancellationToken);
        foreach (string commit in allCommits)
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
                // what this project trusts (idea 202383dc, T1 criterion 1).
                if (!signedByEnrolledNode)
                {
                    continue;
                }

                if (isRevoke)
                {
                    nodes.Remove(nodeId);
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
            }
        }

        return new TrustedOwner(root, publicKeyLine, [.. nodes.Values]);
    }

    /// <summary>
    /// Replays <c>refs/hall9k/ledger/members</c> oldest to newest. The very first commit the ref's
    /// own history ever holds that touches a <c>members/*.yaml</c> path is genesis: accepted only
    /// when self-written (the writer's key traces to the very root fingerprint the file names),
    /// unconditionally the project's first owner-role member either way — win or lose, that slot is
    /// spent once. Every later write needs its signer to already belong to a currently Owner-role
    /// member's own chain; anything else is ignored, including a later self-claimed owner with no
    /// vouch (idea 202383dc, T1 criterion 2).
    /// </summary>
    private async Task<IReadOnlyList<ProjectMember>> ComputeMembersAsync(
        string repositoryPath, IReadOnlyDictionary<string, TrustedOwner> ownerChains, CancellationToken cancellationToken)
    {
        if (!await FetchRefAsync(repositoryPath, MembersRefName, cancellationToken))
        {
            return [];
        }

        string? tip = await ResolveTipAsync(repositoryPath, MembersRefName, cancellationToken);
        if (tip is null)
        {
            return [];
        }

        const string prefix = "members/";
        const string suffix = ".yaml";
        Dictionary<string, ProjectMember> current = [];
        bool genesisDecided = false;

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
                    if (!isDeletion
                        && ownerChains.TryGetValue(fingerprint, out TrustedOwner? selfOwner)
                        && await IsSignedByAsync(repositoryPath, commit, selfOwner.RootPublicKeyLine, cancellationToken))
                    {
                        current[fingerprint] = new ProjectMember(fingerprint, MembershipRole.Owner, ParseIssuedAt(content));
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

                    if (await IsSignedByAnyAsync(repositoryPath, commit, owner, cancellationToken))
                    {
                        authorized = true;
                        break;
                    }
                }

                if (!authorized)
                {
                    continue;
                }

                if (isDeletion)
                {
                    current.Remove(fingerprint);
                }
                else
                {
                    MembershipRole role = ParseRole(content);
                    current[fingerprint] = new ProjectMember(fingerprint, role, ParseIssuedAt(content));
                }
            }
        }

        return [.. current.Values];
    }

    private async Task<bool> IsSignedByAnyAsync(string repositoryPath, string commit, TrustedOwner owner, CancellationToken cancellationToken)
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

    private static MembershipRole ParseRole(string? content)
    {
        string? raw = content is null ? null : ExtractQuotedYamlValue(content, "role");
        return string.Equals(raw, "owner", StringComparison.OrdinalIgnoreCase) ? MembershipRole.Owner : MembershipRole.Member;
    }

    /// <summary>Fetches <paramref name="refName"/> fresh. A missing remote ref is not a failure —
    /// git's exit code for it is indistinguishable from a genuine one, so the distinction is read
    /// from git's own message, the identical reasoning <c>GitLedgerMessageTransport</c>'s own
    /// <c>FetchRefAsync</c> applies.</summary>
    private async Task<bool> FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["fetch", "origin", $"+{refName}:{refName}"], repositoryPath, cancellationToken);
        return result.ExitCode == 0
            || result.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveTipAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        return tip.IsBlank() ? null : tip;
    }

    /// <summary>Every commit reachable from <paramref name="tip"/>, oldest first.</summary>
    private async Task<IReadOnlyList<string>> CommitsOldestFirstAsync(string repositoryPath, string tip, CancellationToken cancellationToken)
    {
        string? output = await RunGitCaptureAsync(repositoryPath, ["log", "--format=%H", "--reverse", tip], cancellationToken);
        return [.. (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every commit reachable from <paramref name="tip"/> that touched <paramref name="path"/>, newest
    /// first — <c>[^1]</c> is therefore the oldest, the one that introduced it.</summary>
    private async Task<IReadOnlyList<string>> CommitsTouchingPathAsync(
        string repositoryPath, string tip, string path, CancellationToken cancellationToken)
    {
        string? output = await RunGitCaptureAsync(repositoryPath, ["log", "--format=%H", tip, "--", path], cancellationToken);
        return [.. (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every path <paramref name="commit"/> added, changed, or removed relative to its own
    /// parent(s) — <c>--root</c> so a commit with no parent (a fresh ref's first commit) diffs
    /// against the empty tree instead of failing.</summary>
    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string repositoryPath, string commit, CancellationToken cancellationToken)
    {
        string? output = await RunGitCaptureAsync(
            repositoryPath, ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", commit], cancellationToken);
        return [.. (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>The content of <paramref name="path"/> at <paramref name="commit"/>'s own tree, or
    /// null when the path is not there — either it never existed at this commit, or (a members
    /// removal) this commit is the one that deleted it.</summary>
    private async Task<string?> ReadAtCommitAsync(string repositoryPath, string commit, string path, CancellationToken cancellationToken) =>
        await RunGitCaptureAsync(repositoryPath, ["show", $"{commit}:{path}"], cancellationToken);

    /// <summary>Verifies <paramref name="commitSha"/> was actually signed by
    /// <paramref name="publicKeyLine"/> — the identical technique
    /// <c>GitLedgerMessageTransport.IsSignedByRegisteredKeyAsync</c> uses (confirming the
    /// <c>gpgsig</c> header itself names an SSH signature before ever trusting
    /// <c>git verify-commit</c>'s own exit code), duplicated rather than shared: a different class
    /// in a different concern, and this narrow, already-proven shape is cheaper to repeat than to
    /// widen a seam neither caller needs generalized (the same call <c>GitLedgerMessageTransport</c>
    /// itself makes about its own private process runner).</summary>
    private async Task<bool> IsSignedByAsync(string repositoryPath, string commitSha, string publicKeyLine, CancellationToken cancellationToken)
    {
        string? committerEmail = (await RunGitCaptureAsync(
            repositoryPath, ["log", "-1", "--format=%ce", commitSha], cancellationToken))?.Trim();
        if (committerEmail.IsBlank())
        {
            return false;
        }

        string? rawCommit = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", commitSha], cancellationToken);
        if (rawCommit is null || !HasSshSignatureHeader(rawCommit))
        {
            return false;
        }

        string allowedSignersFile = Path.Combine(Path.GetTempPath(), $"h9k-chain-allowed-signers-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(allowedSignersFile, $"{committerEmail} {publicKeyLine}\n", cancellationToken);
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
}
