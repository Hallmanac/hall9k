using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Writes a pull request's diff to a file of its own in a run directory before a read-only session starts, for the
/// sessions that read the pull request's diff but cannot run <c>gh</c> to get it (a feedback evaluator, a
/// closeout triage, and every lens of a pr-review run). The same job the pre-flight's <c>pull-request.diff</c> does
/// in <c>RunLauncher</c>, written at a path the session's prompt names, because several sessions can share one run
/// directory.
/// </summary>
internal static partial class PullRequestDiffFile
{
    /// <summary>
    /// Reads <paramref name="pullRequestUrl"/>'s current diff and writes it to <paramref name="path"/>. A diff the daemon
    /// cannot read is written as a short note saying so instead, so the session still starts and its prompt's path
    /// still names a file that explains itself. Never throws for anything but cancellation.
    /// </summary>
    /// <param name="runner">The seam <c>gh</c> is run through, pinned by the daemon to the project's own account.</param>
    /// <param name="pullRequestUrl">The pull request's URL, as the task or run recorded it.</param>
    /// <param name="repositoryPath">The project's repository path, which the runner finds the project's GitHub account by.</param>
    /// <param name="path">The file to write, from <c>RunPaths.FeedbackEvaluatorDiffFile</c> or <c>RunPaths.CloseoutTriageDiffFile</c>.</param>
    public static async Task WriteAsync(
        ProcessRunner runner, string pullRequestUrl, string repositoryPath, string path, ILogger logger,
        CancellationToken cancellationToken)
    {
        string content = await ReadAsync(runner, pullRequestUrl, repositoryPath, logger, cancellationToken);
        await WriteContentAsync(path, content, logger, cancellationToken);
    }

    /// <summary>
    /// Writes the diff of the pull request <paramref name="externalReference"/> names, as it stands at
    /// <paramref name="headOid"/> against <paramref name="baseRefName"/>, to <paramref name="path"/>, unless that file
    /// already exists, so every lens of one pr-review run reads the same diff. The read names the commit in the request
    /// (<see cref="GitHubPullRequestProvider.FetchDiffForCommitAsync"/>), so a push to the pull request after the review
    /// checkout was cut never changes what is written. A diff the daemon cannot read is written as the same note
    /// <see cref="WriteAsync"/> writes. Never throws for anything but cancellation.
    /// </summary>
    /// <param name="externalReference">The task's own external reference, <c>github-pr:owner/repo#42</c>.</param>
    /// <param name="headOid">The commit the run's review checkout was cut at.</param>
    /// <param name="path">The file to write, from <c>RunPaths.ReviewDiffFile</c>.</param>
    public static async Task EnsureWrittenForCommitAsync(
        ProcessRunner runner, string? externalReference, string baseRefName, string headOid, string repositoryPath,
        string path, ILogger logger, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        string content = await ReadForCommitAsync(
            runner, externalReference, baseRefName, headOid, repositoryPath, logger, cancellationToken);
        await WriteContentAsync(path, content, logger, cancellationToken);
    }

    /// <summary>
    /// What <see cref="EnsureWrittenForCommitAsync"/> does for a caller that holds no head commit: the commit is read
    /// from the review checkout at <paramref name="checkoutPath"/>, which is detached at the commit the run was cut at,
    /// and never from the pull request's current head. A checkout whose commit cannot be read leaves the note instead.
    /// </summary>
    public static async Task EnsureWrittenForCheckoutAsync(
        ProcessRunner runner, string? externalReference, string baseRefName, string checkoutPath, string repositoryPath,
        string path, ILogger logger, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        string? headOid = await ReadCheckoutHeadAsync(runner, checkoutPath, logger, cancellationToken);
        string content = headOid is null
            ? Unreadable("the commit the review checkout holds could not be read")
            : await ReadForCommitAsync(
                runner, externalReference, baseRefName, headOid, repositoryPath, logger, cancellationToken);
        await WriteContentAsync(path, content, logger, cancellationToken);
    }

    private static async Task WriteContentAsync(string path, string content, ILogger logger, CancellationToken cancellationToken)
    {
        string staging = path + ".partial";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the file and moved into place, so a write that fails partway never leaves a cut-short diff
            // that a later lens, which keeps whatever file it finds, would read as the whole change. Nothing reads the
            // staging file, so one a failed write leaves behind is harmless.
            await File.WriteAllTextAsync(staging, content, cancellationToken);
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The pull request's diff could not be written to {Path}", path);
        }
    }

    private static async Task<string> ReadAsync(
        ProcessRunner runner, string pullRequestUrl, string repositoryPath, ILogger logger,
        CancellationToken cancellationToken)
    {
        // Only an https URL goes to gh as its argument: a value that starts with a dash would be read as a flag.
        if (!pullRequestUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Unreadable("the task records no pull request address to read it from");
        }

        try
        {
            ProcessResult result = await runner("gh", ["pr", "diff", pullRequestUrl], repositoryPath, cancellationToken);
            if (result.ExitCode == 0)
            {
                return result.StandardOutput;
            }

            logger.LogWarning(
                "gh pr diff for {PullRequestUrl} exited {ExitCode}: {Error}",
                pullRequestUrl, result.ExitCode, RelayedText.OneLine(result.StandardError));
            return Unreadable("gh could not read it");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "gh pr diff for {PullRequestUrl} could not be run", pullRequestUrl);
            return Unreadable("gh could not be run");
        }
    }

    private static async Task<string> ReadForCommitAsync(
        ProcessRunner runner, string? externalReference, string baseRefName, string headOid, string repositoryPath,
        ILogger logger, CancellationToken cancellationToken)
    {
        if (!GitHubPullRequestReference.TryParseCanonical(
                ExternalReference.Parse(externalReference).Reference, out string repository, out int number))
        {
            return Unreadable("the task records no pull request to read it from");
        }

        // Both go into the API path, so a value that is not plainly a commit or a branch name is never sent.
        if (!CommitPattern().IsMatch(headOid) || baseRefName.IsBlank() || baseRefName.StartsWith('-') || baseRefName.Contains(".."))
        {
            return Unreadable("the run records no commit to read it at");
        }

        try
        {
            return await new GitHubPullRequestProvider(runner).FetchDiffForCommitAsync(
                repository, number, baseRefName, headOid, repositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "The diff of {Repository}#{Number} at {HeadOid} could not be read: {Error}",
                repository, number, headOid, RelayedText.OneLine(exception.Message));
            return Unreadable("gh could not read it");
        }
    }

    private static async Task<string?> ReadCheckoutHeadAsync(
        ProcessRunner runner, string checkoutPath, ILogger logger, CancellationToken cancellationToken)
    {
        // A blank path would run git in the daemon's own working directory and read some other repository's commit.
        if (checkoutPath.IsBlank() || !Directory.Exists(checkoutPath))
        {
            logger.LogWarning("The review checkout {CheckoutPath} is not there to read its commit from", checkoutPath);
            return null;
        }

        try
        {
            ProcessResult result = await runner("git", ["rev-parse", "--verify", "HEAD"], checkoutPath, cancellationToken);
            string oid = result.StandardOutput.Trim();
            if (result.ExitCode == 0 && CommitPattern().IsMatch(oid))
            {
                return oid;
            }

            logger.LogWarning(
                "git rev-parse HEAD in {CheckoutPath} exited {ExitCode}: {Error}",
                checkoutPath, result.ExitCode, RelayedText.OneLine(result.StandardError));
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "git rev-parse HEAD in {CheckoutPath} could not be run", checkoutPath);
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    private static string Unreadable(string reason) =>
        $"The daemon could not read this pull request's diff before your session started ({reason}). "
        + "There is no diff in this file. Read the change from the checkout instead, with git.\n";
}
