using System.Diagnostics;
using System.Text;
using Hall9k.Connectors.Processes;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Ledger;

/// <summary>
/// <see cref="ILedger"/> over the project's bare repository, by git plumbing alone. Every
/// operation fetches with an explicit, one-off refspec (never the project's own configured
/// <c>remote.origin.fetch</c>) and builds each commit through a private <c>GIT_INDEX_FILE</c> —
/// never a working tree, never touched by anything else running against the same bare repository.
/// </summary>
public sealed class GitLedger(ILogger<GitLedger> logger) : ILedger
{
    /// <summary>
    /// How many times a rejected push retries before <see cref="LedgerPushRejectedException"/>.
    /// Bounded rather than unbounded: two writers racing the same ref converge within a round or
    /// two in practice (each round costs one fetch and one push), so a push still losing after
    /// this many is fighting something a retry loop cannot fix on its own.
    /// </summary>
    private const int MaxPushAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Test-only seam: when set, invoked once per retry attempt immediately before the push, so a
    /// test can land a second writer's real commit on the shared origin at a precise moment and
    /// force a genuine git-level rejection deterministically, rather than racing real processes
    /// and hoping the scheduler cooperates. Never set outside a test.
    /// </summary>
    internal Func<int, CancellationToken, Task>? BeforePushForTesting { get; set; }

    public async Task<LedgerFile> ReadAsync(
        string repositoryPath, string refName, string path, CancellationToken cancellationToken)
    {
        RequireRegistered(refName);
        await FetchRefAsync(repositoryPath, refName, cancellationToken);
        string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
        return await ReadAtTipAsync(repositoryPath, tip, path, cancellationToken);
    }

    public async Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
    {
        RequireRegistered(request.RefName);
        await FetchRefAsync(request.RepositoryPath, request.RefName, cancellationToken);

        string lastError = string.Empty;
        for (int attempt = 1; attempt <= MaxPushAttempts; attempt++)
        {
            string? tip = await ResolveTipAsync(request.RepositoryPath, request.RefName, cancellationToken);
            LedgerFile current = await ReadAtTipAsync(request.RepositoryPath, tip, request.Path, cancellationToken);
            if (current.BlobId != request.ExpectedBlobId)
            {
                return LedgerWriteOutcome.Conflict(current);
            }

            string commitId = await BuildCommitAsync(request, tip, cancellationToken);
            await UpdateLocalRefAsync(request.RepositoryPath, request.RefName, commitId, tip, cancellationToken);

            if (BeforePushForTesting is { } beforePush)
            {
                await beforePush(attempt, cancellationToken);
            }

            (int pushExit, _, string pushError) = await RunGitAsync(
                request.RepositoryPath,
                ["push", "origin", $"{request.RefName}:{request.RefName}"],
                environment: null,
                standardInput: null,
                cancellationToken);
            if (pushExit == 0)
            {
                return LedgerWriteOutcome.Written(commitId);
            }

            lastError = pushError;
            logger.LogInformation(
                "Push to {RefName} was rejected on attempt {Attempt}/{MaxAttempts} ({Error}); re-fetching to retry",
                request.RefName, attempt, MaxPushAttempts, pushError.Trim());

            // Force-refetch: our own local ref was just moved (above) to the rejected commit, so
            // this is what reconciles it back to the remote's real tip before the loop's next
            // iteration re-reads it.
            await FetchRefAsync(request.RepositoryPath, request.RefName, cancellationToken);
        }

        throw new LedgerPushRejectedException(request.RefName, MaxPushAttempts, lastError);
    }

    private static void RequireRegistered(string refName)
    {
        if (!LedgerRefRegistry.IsRegistered(refName))
        {
            throw new ArgumentException(
                $"{refName} is not registered in {nameof(LedgerRefRegistry)} — every ref a caller "
                + "uses must be registered there first, so an ordinary fetch never brings it down "
                + "and nothing else ever touches it.",
                nameof(refName));
        }
    }

    private async Task FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        (int exitCode, _, string error) = await RunGitAsync(
            repositoryPath, ["fetch", "origin", $"+{refName}:{refName}"], null, null, cancellationToken);
        if (exitCode != 0)
        {
            // Expected the first time anything writes to a ref nobody has pushed yet ("couldn't
            // find remote ref") — logged rather than thrown so a genuine network problem is still
            // visible, without treating the routine "nothing here yet" case as a failure.
            logger.LogDebug(
                "Fetch of {RefName} from origin in {Repository} found nothing to bring down ({Error}) "
                + "— proceeding as though the ref does not exist there yet",
                refName, repositoryPath, error.Trim());
        }
    }

    private static async Task<string?> ResolveTipAsync(
        string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await RunGitAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], null, null, cancellationToken);
        return exitCode == 0 ? output.Trim() : null;
    }

    private static async Task<LedgerFile> ReadAtTipAsync(
        string repositoryPath, string? tip, string path, CancellationToken cancellationToken)
    {
        if (tip is null)
        {
            return LedgerFile.Absent;
        }

        (int blobExit, string blobOutput, _) = await RunGitAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{tip}:{path}"], null, null, cancellationToken);
        if (blobExit != 0)
        {
            return LedgerFile.Absent;
        }

        string blobId = blobOutput.Trim();
        (int catExit, string content, string catError) = await RunGitAsync(
            repositoryPath, ["cat-file", "-p", blobId], null, null, cancellationToken);
        if (catExit != 0)
        {
            throw new InvalidOperationException($"git cat-file -p {blobId} failed in {repositoryPath}: {catError.Trim()}");
        }

        return new LedgerFile(content, blobId);
    }

    /// <summary>
    /// Builds the new tree (<see cref="BuildTreeAsync"/>) and commits it as a child of
    /// <paramref name="parentTip"/> (or a root commit when the ref does not exist yet), signed
    /// per invocation when <see cref="LedgerWriteRequest.SigningKey"/> is present. Every config
    /// override — identity, signing format, signing key — is passed as its own <c>-c</c> on this
    /// one command line rather than written anywhere, so it never touches this repository's or
    /// this machine's git config and never outlives this single process.
    /// </summary>
    private static async Task<string> BuildCommitAsync(
        LedgerWriteRequest request, string? parentTip, CancellationToken cancellationToken)
    {
        string treeId = await BuildTreeAsync(request.RepositoryPath, request.Path, request.Content, parentTip, cancellationToken);

        List<string> arguments =
        [
            "-c", $"user.name={request.Committer.Name}",
            "-c", $"user.email={request.Committer.Email}",
        ];
        if (request.SigningKey is { } signingKey)
        {
            arguments.Add("-c");
            arguments.Add("gpg.format=ssh");
            arguments.Add("-c");
            arguments.Add($"user.signingkey={signingKey.PrivateKeyPath}");
        }

        arguments.Add("commit-tree");
        arguments.Add(treeId);
        if (parentTip is not null)
        {
            arguments.Add("-p");
            arguments.Add(parentTip);
        }

        if (request.SigningKey is not null)
        {
            arguments.Add("-S");
        }

        arguments.Add("-m");
        arguments.Add(request.CommitMessage);

        (int exitCode, string output, string error) = await RunGitAsync(
            request.RepositoryPath, arguments, null, null, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git commit-tree failed in {request.RepositoryPath}: {error.Trim()}");
        }

        return output.Trim();
    }

    /// <summary>
    /// The new tree, built through a private index this call owns start to finish — never the
    /// repository's own (a bare repository has none) and never shared with any other concurrent
    /// call, so two writers building trees for the same ref at once never see each other's
    /// half-built state. <paramref name="parentTip"/>'s own tree is loaded first when it exists, so
    /// every path other than <paramref name="path"/> survives into the new tree unchanged — what
    /// lets two writers on two different paths both land without either clobbering the other.
    /// </summary>
    private static async Task<string> BuildTreeAsync(
        string repositoryPath, string path, string content, string? parentTip, CancellationToken cancellationToken)
    {
        string tempIndex = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"h9k-ledger-index-{Guid.NewGuid():N}");
        Dictionary<string, string> indexEnvironment = new() { ["GIT_INDEX_FILE"] = tempIndex };
        try
        {
            if (parentTip is not null)
            {
                (int readExit, _, string readError) = await RunGitAsync(
                    repositoryPath, ["read-tree", parentTip], indexEnvironment, null, cancellationToken);
                if (readExit != 0)
                {
                    throw new InvalidOperationException($"git read-tree {parentTip} failed in {repositoryPath}: {readError.Trim()}");
                }
            }

            (int hashExit, string blobOutput, string hashError) = await RunGitAsync(
                repositoryPath, ["hash-object", "-w", "--stdin"], null, content, cancellationToken);
            if (hashExit != 0)
            {
                throw new InvalidOperationException($"git hash-object failed in {repositoryPath}: {hashError.Trim()}");
            }

            string blobId = blobOutput.Trim();
            (int addExit, _, string addError) = await RunGitAsync(
                repositoryPath,
                ["update-index", "--add", "--cacheinfo", $"100644,{blobId},{path}"],
                indexEnvironment, null, cancellationToken);
            if (addExit != 0)
            {
                throw new InvalidOperationException($"git update-index failed in {repositoryPath}: {addError.Trim()}");
            }

            (int writeExit, string treeOutput, string writeError) = await RunGitAsync(
                repositoryPath, ["write-tree"], indexEnvironment, null, cancellationToken);
            if (writeExit != 0)
            {
                throw new InvalidOperationException($"git write-tree failed in {repositoryPath}: {writeError.Trim()}");
            }

            return treeOutput.Trim();
        }
        finally
        {
            try
            {
                File.Delete(tempIndex);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temp file; nothing downstream reads it again. Caught
                // narrowly (not bare Exception) so this finally block can never turn a successful
                // BuildTreeAsync into a failure over cleanup alone, while still letting a genuine
                // defect elsewhere in this method surface.
            }
        }
    }

    /// <summary>
    /// Compare-and-swap local update, the same create-only/expected-old-value shape
    /// <c>GitWorktreeManager</c> already uses for a branch ref: an empty <paramref name="oldTip"/>
    /// means "must not exist locally yet", matching a fresh ref's first commit.
    /// </summary>
    private static async Task UpdateLocalRefAsync(
        string repositoryPath, string refName, string newCommit, string? oldTip, CancellationToken cancellationToken)
    {
        (int exitCode, _, string error) = await RunGitAsync(
            repositoryPath, ["update-ref", refName, newCommit, oldTip ?? ""], null, null, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git update-ref {refName} failed in {repositoryPath}: {error.Trim()}");
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = standardInput is not null ? Utf8NoBom : null,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryPath);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        NonInteractiveGit.Apply(process.StartInfo);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, await standardOutput, await standardError);
        }
        catch
        {
            // Cancelling the wait above only stops Hall9k waiting: git is a real operating-system
            // process and keeps running past it, so a cancelled fetch or push could otherwise still
            // land on origin or hold a repository lock after this method has already returned to a
            // caller that believes it was cancelled. Ended here, the same way ExternalProcess's own
            // runner ends a tool it stopped waiting on, before the exception — cancellation or any
            // other failure — is let through.
            await TerminateAsync(process);
            throw;
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (Exception)
        {
            // Nothing here is recoverable and nothing here is the caller's problem.
        }
    }
}
