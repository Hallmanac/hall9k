using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Writes a pull request's diff to a file of its own in a run directory before a read-only session starts, for the
/// sessions that read the pull request's diff but cannot run <c>gh</c> to get it (a feedback evaluator and a
/// closeout triage). The same job the pre-flight's <c>pull-request.diff</c> does in <c>RunLauncher</c>, written once
/// per session at a path that session's prompt names, because several sessions can share one run directory.
/// </summary>
internal static class PullRequestDiffFile
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
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken);
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

    private static string Unreadable(string reason) =>
        $"The daemon could not read this pull request's diff before your session started ({reason}). "
        + "There is no diff in this file. Read the change from the checkout instead, with git.\n";
}
