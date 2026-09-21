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
                return false;
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

    private async Task<string?> RunGitCaptureAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }
}
