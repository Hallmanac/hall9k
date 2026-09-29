using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// The real <see cref="ILedgerCommitAccess"/>: every git-plumbing call
/// <see cref="GitLedgerChainReader"/> used to make directly, moved behind the seam with no behavior
/// change at all — every method here is the identical git invocation, byte for byte, that
/// <see cref="GitLedgerChainReader"/>'s own private helpers used to run. Constructed fresh per
/// <see cref="GitLedgerChainReader.ComputeAsync"/> call, bound to that one call's own repository path.
/// </summary>
internal sealed class GitPlumbingLedgerCommitAccess(ProcessRunner runner, string repositoryPath) : ILedgerCommitAccess
{
    /// <summary>The only principal this class ever writes into a temporary <c>allowed_signers</c>
    /// file — a fixed literal, never the commit's own committer email. See
    /// <see cref="GitLedgerChainReader"/>'s own doc comment on its former copy of this constant for
    /// the injection this closes.</summary>
    private const string AllowedSignersPrincipal = "hall9k-chain-writer";

    public async Task<IReadOnlyList<string>> DiscoverRefSuffixesAsync(string prefix, CancellationToken cancellationToken)
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

    public Task<LedgerAppendOnlyFetchResult> FetchRefAsync(string refName, CancellationToken cancellationToken) =>
        LedgerAppendOnlyRefFetcher.FetchAsync(runner, repositoryPath, refName, cancellationToken);

    public Task<IReadOnlyDictionary<string, LedgerAppendOnlyFetchResult>> FetchPrefixAsync(
        string prefix, IReadOnlyList<string> suffixes, CancellationToken cancellationToken) =>
        LedgerAppendOnlyRefFetcher.FetchPrefixAsync(runner, repositoryPath, prefix, suffixes, cancellationToken);

    /// <summary>Every commit reachable from <paramref name="tip"/>, oldest first —
    /// <c>--topo-order --first-parent</c> so replay follows the actual mainline commit graph rather
    /// than committer-date order (freely chosen by whoever signs a commit, and never consulted
    /// anywhere in this walk) and never descends into a merge's second parent at all, closing the
    /// path a backdated, merged-in commit would otherwise use to reorder itself earlier in ref
    /// history (independent pre-PR review, cycle 1, adversarial lens, medium).</summary>
    public async Task<IReadOnlyList<string>> CommitsOldestFirstAsync(string tip, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            ["log", "--format=%H", "--reverse", "--topo-order", "--first-parent", tip], cancellationToken);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every commit reachable from <paramref name="tip"/> (mainline only —
    /// <c>--topo-order --first-parent</c>, the identical reasoning <see cref="CommitsOldestFirstAsync"/>
    /// applies) that touched <paramref name="path"/>, newest first — <c>[0]</c> is therefore the
    /// commit that currently produces whatever content a caller just read at <paramref name="tip"/>.</summary>
    public async Task<IReadOnlyList<string>> CommitsTouchingPathAsync(string tip, string path, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            ["log", "--format=%H", "--topo-order", "--first-parent", tip, "--", path], cancellationToken);
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
    public async Task<IReadOnlyList<string>> ChangedPathsAsync(string commit, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(
            ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "--diff-merges=first-parent", commit],
            cancellationToken);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>The content of <paramref name="path"/> at <paramref name="commit"/>'s own tree, or
    /// null when the path is not there — either it never existed at this commit, or (a members
    /// removal) this commit is the one that deleted it. Only that specific, documented git message
    /// is read as absence; any other failure (a corrupt or incomplete local object, disk I/O) is
    /// thrown instead of silently read as a deletion, which would let a broken local read change
    /// what this walk trusts rather than simply fail.</summary>
    public async Task<string?> ReadAtCommitAsync(string commit, string path, CancellationToken cancellationToken)
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

    /// <summary>The committer time of <paramref name="commit"/>, only ever used to order two declarations of the same field against each other (a rename), never as a trust anchor.</summary>
    public async Task<DateTimeOffset> CommitTimeAsync(string commit, CancellationToken cancellationToken)
    {
        string output = await RunGitCaptureOrThrowAsync(["log", "-1", "--format=%cI", commit], cancellationToken);
        return DateTimeOffset.TryParse(
            output.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    /// <summary>Verifies <paramref name="commit"/> was actually signed by
    /// <paramref name="publicKeyLine"/> — confirming the <c>gpgsig</c> header itself names an SSH
    /// signature before ever trusting <c>git verify-commit</c>'s own exit code, and writing only the
    /// fixed <see cref="AllowedSignersPrincipal"/> into the temporary <c>allowed_signers</c> file —
    /// never the commit's own committer email, which the commit's own author controls.</summary>
    public async Task<bool> IsSignedByAsync(string commit, string publicKeyLine, CancellationToken cancellationToken)
    {
        string? rawCommit = await RunGitCaptureAsync(["cat-file", "commit", commit], cancellationToken);
        if (rawCommit is null || !GitLedgerChainReader.HasSshSignatureHeader(rawCommit))
        {
            return false;
        }

        return await VerifyAllowedSignerAsync(commit, publicKeyLine, cancellationToken);
    }

    /// <summary>
    /// Verifies raw commit bytes an offline bundle embedded, without ever fetching from wherever
    /// they came from: injects them as a loose object into this repository (<c>git hash-object -w
    /// -t commit</c>) and runs the identical <see cref="IsSignedByAsync"/> check against the sha
    /// that injection computes — a git object is content-addressed, so the injected object's own
    /// sha is identical to whatever it was in the repository it was read from, and
    /// <c>git verify-commit</c> needs nothing beyond the commit object itself (never its tree or
    /// parents) to check a signature over it.
    /// </summary>
    public async Task<bool> IsSignedByRawBytesAsync(string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken)
    {
        if (!GitLedgerChainReader.HasSshSignatureHeader(rawCommitBytes))
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
            return await IsSignedByAsync(injectedSha, publicKeyLine, cancellationToken);
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

    private async Task<bool> VerifyAllowedSignerAsync(string commitSha, string publicKeyLine, CancellationToken cancellationToken)
    {
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

    private async Task<string?> RunGitCaptureAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    /// <summary>For a walk that has already confirmed <c>tip</c> or <c>commit</c> resolves: a log
    /// or diff-tree over a commit this walk already knows exists has no legitimate failure mode, so
    /// unlike <see cref="RunGitCaptureAsync"/> a non-zero exit here is thrown rather than folded
    /// into "no commits"/"no changed paths" — a corrupt pack or a local git failure must stop the
    /// walk, never silently read as though that commit changed nothing at all.</summary>
    private async Task<string> RunGitCaptureOrThrowAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
