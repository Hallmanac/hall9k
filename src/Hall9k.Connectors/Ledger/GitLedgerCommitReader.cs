using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Connectors.Ledger;

/// <summary>
/// The real <see cref="ILedgerCommitReader"/>: git plumbing alone, over a repository this install
/// already has locally (never a live fetch of a third party's remote — the caller's own repository,
/// already cloned for its own project). Duplicates <c>GitLedgerChainReader</c>'s own small,
/// already-proven fetch/resolve/read helpers rather than sharing them across the namespace boundary —
/// the identical reasoning that class' own doc gives for duplicating <c>IsSignedByAsync</c> rather
/// than generalizing a seam neither caller needs widened.
/// </summary>
public sealed class GitLedgerCommitReader(ProcessRunner? runner = null) : ILedgerCommitReader
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    public async Task<LedgerSignedCommit?> ReadSignedCommitAsync(
        string repositoryPath, string refName, string path, CancellationToken cancellationToken)
    {
        await FetchRefAsync(repositoryPath, refName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        if (tip is null)
        {
            return null;
        }

        string? content = await ReadAtCommitAsync(repositoryPath, tip, path, cancellationToken);
        if (content is null)
        {
            return null;
        }

        // [0], the newest commit reachable from tip that touched this path — the one that actually
        // produced the content just read above, the identical convention GitLedgerChainReader's own
        // CommitsTouchingPathAsync documents.
        IReadOnlyList<string> commits = await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken);
        if (commits.Count == 0)
        {
            return null;
        }

        string sha = commits[0];
        string? rawBytes = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", sha], cancellationToken);
        return rawBytes is null ? null : new LedgerSignedCommit(content, sha, rawBytes);
    }

    public async Task<IReadOnlyList<LedgerPathCommit>> ReadCommitsTouchingPathAsync(
        string repositoryPath, string refName, string path,
        Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken)
    {
        await FetchRefAsync(repositoryPath, refName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        if (tip is null)
        {
            return [];
        }

        // The ref's own WHOLE first-parent history, never limited by a `git log -- path`
        // pathspec: a signed reissue of already-current content (the owner re-asserting exactly
        // what a member's own unauthorized commit already left at the tip) produces a commit that
        // is TREESAME for `path` to its own parent, and `git log -- path` prunes a TREESAME commit
        // unconditionally — no flag restores it, verified in a scratch repository — so a caller
        // walking a path-filtered log can never see that commit at all, and the owner's own
        // reissue is silently lost (independent pre-PR review, cycle 3, both lenses, high).
        // Membership in this method's own result is decided below by
        // LedgerCommitPathTrailer.PathsWrittenBy instead — GitLedger stamps every commit it
        // builds with the exact path(s) it writes or deletes, inside the signed message itself,
        // which is the only signal that can ever tell "this commit deliberately reissues `path`
        // with unchanged bytes" apart from "this commit never touched `path` at all": a tree diff
        // cannot, since both produce an identical tree for `path` relative to the commit's own
        // parent. Reading every commit's raw trailer before deciding to keep it is what closes the
        // laundering this walk opened by dropping the pathspec: an owner-authorized commit to some
        // OTHER path built on top of a member's own unauthorized overwrite of `path` no longer
        // vouches for whatever `path` happened to read as in that commit's own inherited tree
        // (independent pre-PR review, cycle 5, conformance and adversarial lenses, both high).
        IReadOnlyList<string> commits = await CommitShasAsync(repositoryPath, tip, cancellationToken);

        // Lazily populated, and only once per call, the moment the first trailer-less commit is
        // seen below: every commit GitLedger builds today stamps at least one Hall9k-Ledger-Path
        // line, so a commit with none at all is real history from before this trailer existed,
        // never a same-shape commit this fix needs to distrust. Falling back to the old
        // tree-diff membership test for exactly those commits — never for a trailer-bearing one,
        // which always answers from the trailer alone — is what keeps a ledger path written
        // entirely before this fix shipped (this project's own live prompt-addenda ref included)
        // materializing exactly as it did before, rather than reading as untouched and vanishing
        // the moment this walk can no longer find a trailer that was never stamped on it.
        IReadOnlyList<string>? legacyCommitsTouchingPath = null;
        List<LedgerPathCommit> results = new();
        foreach (string sha in commits)
        {
            string? rawBytes = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", sha], cancellationToken);
            if (rawBytes is null)
            {
                // A corrupt or missing local object for a commit this same walk just named as
                // reachable — genuinely exceptional, never a legitimate "this commit does not
                // exist" the rest of this method's caller should read as absence.
                throw new InvalidOperationException($"git cat-file commit {sha} failed in {repositoryPath}.");
            }

            IReadOnlyList<string> pathsWritten = LedgerCommitPathTrailer.PathsWrittenBy(rawBytes);
            bool touchesPath = pathsWritten.Count > 0
                ? pathsWritten.Contains(path, StringComparer.Ordinal)
                : (legacyCommitsTouchingPath ??=
                    await CommitsTouchingPathAsync(repositoryPath, tip, path, cancellationToken))
                    .Contains(sha, StringComparer.Ordinal);
            if (!touchesPath)
            {
                // Either a trailer-bearing commit that never claimed `path` — never counted as a
                // candidate, and never handed to isAuthorizedAsync, which exists to answer "is
                // THIS commit's own claim about `path` trustworthy," not "is this commit signed at
                // all" — or a pre-trailer commit whose own tree diff says it never touched `path`
                // either.
                continue;
            }

            string? content = await ReadAtCommitAsync(repositoryPath, sha, path, cancellationToken);
            results.Add(new LedgerPathCommit(content, sha, rawBytes));

            // Stops walking the ref's own history — never spawning another git process for an
            // older commit — the moment isAuthorizedAsync accepts one: the caller's own owner test
            // (PromptAddendaSweepEngine.MaterializeAsync, NewestCommitIsOwnerAuthorizedAsync) only
            // ever wants the FIRST (newest) commit it authorizes, so a repository collaborator who
            // pushes thousands of unsigned commits ahead of the last authorized one costs this
            // walk only as many git processes as commits actually stand between the tip and that
            // authorized commit, never the ref's entire history (independent pre-PR review, cycle
            // 3, adversarial lens, medium).
            if (await isAuthorizedAsync(rawBytes, cancellationToken))
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Whether <paramref name="refName"/>'s own literal tip commit — never "the newest commit
    /// touching some path," which <see cref="ReadCommitsTouchingPathAsync"/> answers instead — is
    /// itself accepted by <paramref name="isAuthorizedAsync"/>. <c>true</c> when the ref does not
    /// exist yet: there is nothing at the tip to override.
    /// </summary>
    public async Task<bool> IsRefTipAuthorizedAsync(
        string repositoryPath, string refName,
        Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken)
    {
        await FetchRefAsync(repositoryPath, refName, cancellationToken);

        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        if (tip is null)
        {
            return true;
        }

        string? rawBytes = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", tip], cancellationToken);
        if (rawBytes is null)
        {
            throw new InvalidOperationException($"git cat-file commit {tip} failed in {repositoryPath}.");
        }

        return await isAuthorizedAsync(rawBytes, cancellationToken);
    }

    public async Task<bool> IsSignedByAsync(
        string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken)
    {
        // GitLedgerChainReader.HasSshSignatureHeader is internal, same assembly — reused directly
        // rather than duplicated a third time (GitLedgerChainReader.IsSignedByAsync and
        // IsSignedByRawBytesAsync already share the identical check between themselves).
        if (!GitLedgerChainReader.HasSshSignatureHeader(rawCommitBytes))
        {
            return false;
        }

        string tempCommitFile = Path.Combine(Path.GetTempPath(), $"h9k-carry-precheck-commit-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(tempCommitFile, rawCommitBytes, cancellationToken);
            ProcessResult hashResult = await runner(
                "git", ["hash-object", "-w", "-t", "commit", tempCommitFile], repositoryPath, cancellationToken);
            if (hashResult.ExitCode != 0)
            {
                // Thrown, never returned as false: this step has not even reached
                // git verify-commit yet, so a non-zero exit here is an infrastructure failure (a
                // disk or permissions problem, an unavailable git binary) rather than a genuine
                // "not signed" verdict — a caller that caches this return value against an unmoved
                // ref tip (InviteSweepEngine's own _lastKnownRefs) must never mistake a transient
                // hiccup here for a real signature failure and cache it as one forever (independent
                // pre-PR review, cycle 1, conformance lens, low).
                throw new InvalidOperationException(
                    $"git hash-object -w -t commit {tempCommitFile} failed in {repositoryPath} (exit "
                    + $"{hashResult.ExitCode}): {hashResult.StandardError.Trim()}");
            }

            string injectedSha = hashResult.StandardOutput.Trim();
            string allowedSignersFile = Path.Combine(Path.GetTempPath(), $"h9k-carry-precheck-signers-{Guid.NewGuid():N}");
            try
            {
                // A fixed principal, never the commit's own committer email — the identical
                // injection GitLedgerChainReader.IsSignedByAsync's own doc comment explains.
                await File.WriteAllTextAsync(allowedSignersFile, $"hall9k-chain-writer {publicKeyLine}\n", cancellationToken);
                ProcessResult verifyResult = await runner(
                    "git",
                    ["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={allowedSignersFile}", "verify-commit", injectedSha],
                    repositoryPath,
                    cancellationToken);
                return verifyResult.ExitCode == 0;
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

    private async Task FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["fetch", "origin", $"+{refName}:{refName}"], repositoryPath, cancellationToken);
        if (result.ExitCode == 0 || result.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"git fetch of {refName} from origin in {repositoryPath} failed (exit {result.ExitCode}): "
            + $"{result.StandardError.Trim()}");
    }

    private async Task<string?> ResolveTipAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        return tip.IsBlank() ? null : tip;
    }

    private async Task<string?> ReadAtCommitAsync(string repositoryPath, string commit, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["show", $"{commit}:{path}"], repositoryPath, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        return result.StandardError.Contains("does not exist in", StringComparison.OrdinalIgnoreCase)
            ? null
            : throw new InvalidOperationException(
                $"git show {commit}:{path} failed in {repositoryPath} (exit {result.ExitCode}): {result.StandardError.Trim()}");
    }

    /// <summary>Every commit reachable from <paramref name="tip"/> that touched <paramref name="path"/>
    /// — used only by <see cref="ReadSignedCommitAsync"/>, which wants the specific commit that
    /// actually produced the content it just read at <paramref name="path"/>'s current tip, never a
    /// commit whose own tree for that path happens to be unchanged. <see cref="CommitShasAsync"/> is
    /// the sibling <see cref="ReadCommitsTouchingPathAsync"/> uses instead, precisely because THAT
    /// caller's own owner test needs a reissue commit this path-filtered walk would prune.</summary>
    private async Task<IReadOnlyList<string>> CommitsTouchingPathAsync(
        string repositoryPath, string tip, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "git", ["log", "--format=%H", "--topo-order", "--first-parent", tip, "--", path], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git log --first-parent {tip} -- {path} failed in {repositoryPath}: {result.StandardError.Trim()}");
        }

        return [.. result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    /// <summary>Every commit reachable from <paramref name="tip"/>, first-parent order, newest
    /// first — the ref's own real history, with no pathspec at all, so a commit that is TREESAME
    /// to its parent for any one path is still included: dropping the pathspec from the
    /// <c>git log</c> call is what actually fixes the owner-reissue defect
    /// <see cref="ReadCommitsTouchingPathAsync"/>'s own doc names, since no combination of flags
    /// makes a path-filtered <c>git log</c> report a TREESAME commit.</summary>
    private async Task<IReadOnlyList<string>> CommitShasAsync(string repositoryPath, string tip, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "git", ["log", "--format=%H", "--topo-order", "--first-parent", tip], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git log --first-parent {tip} failed in {repositoryPath}: {result.StandardError.Trim()}");
        }

        return [.. result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    private async Task<string?> RunGitCaptureAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }
}
