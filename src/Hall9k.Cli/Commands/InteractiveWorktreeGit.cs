using System.Diagnostics;
using Hall9k.Connectors.Worktrees;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The small set of git operations an interactive claim's own CLI commands need directly —
/// checking for uncommitted work, reading HEAD, and pushing the branch — none of which touch
/// the daemon's gate-running or PR-opening machinery (<c>Hall9k.Daemon.Execution.VerificationRunner</c>,
/// <c>PullRequestOpener</c>): the CLI cannot reference <c>Hall9k.Daemon</c> (it never hosts
/// Wolverine), and once <c>h9k task deliver</c> hands a run to the daemon's own pipeline
/// (<c>RunSupervisor.ResumeStrandedPipelinesAsync</c>) it re-verifies with the real thing anyway
/// — nothing here needs the retry/infra-classification/test-scoping sophistication that
/// machinery carries, only an honest "is this worktree clean, and what does HEAD look like".
/// </summary>
internal static class InteractiveWorktreeGit
{
    /// <summary>
    /// Every tracked file the worktree holds modified or staged, or null when git could not be
    /// asked (never guessed at as clean). The parsing itself — the subtle part — is
    /// <see cref="WorktreeGitStatus.ParsePorcelain"/>, shared with
    /// <c>VerificationRunner.ListUncommittedFilesAsync</c> rather than duplicated: an untracked
    /// file is often a gate byproduct the project's .gitignore has not caught up with, never
    /// blocking, and that split is exactly what the shared parser hands back.
    /// </summary>
    public static async Task<(IReadOnlyList<string>? Modified, IReadOnlyList<string> Untracked)> ListUncommittedFilesAsync(
        string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await RunGitAsync(
            worktreePath, ["status", "--porcelain", "-z", "--untracked-files=all"], cancellationToken);
        if (exitCode != 0)
        {
            return (null, []);
        }

        (IReadOnlyList<string> modified, IReadOnlyList<string> untracked) = WorktreeGitStatus.ParsePorcelain(output);
        return (modified, untracked);
    }

    public static async Task<string?> GetHeadShaAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await RunGitAsync(worktreePath, ["rev-parse", "HEAD"], cancellationToken);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// The branch's first push: plain, never force — a fresh branch cut by h9k task work has no
    /// remote history to overwrite. Any later rewrite (the review loop's own fixup/rebase
    /// doctrine, once this branch hands into the standard pipeline) is pushed by the daemon's own
    /// PullRequestOpener, unconditionally with --force-with-lease so it always protects a tip
    /// this push already published.
    /// </summary>
    public static async Task<(bool Succeeded, string Error)> PushAsync(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        (int exitCode, _, string standardError) = await RunGitAsync(
            worktreePath, ["push", "-u", "origin", branch], cancellationToken);
        return (exitCode == 0, standardError.Trim());
    }

    public static async Task<int> CountBranchCommitsAsync(
        string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        foreach (string baseRef in new[] { $"origin/{baseBranch}", baseBranch })
        {
            (int exitCode, string output, _) = await RunGitAsync(
                worktreePath, ["rev-list", "--count", $"{baseRef}..HEAD"], cancellationToken);
            if (exitCode == 0 && int.TryParse(output.Trim(), out int count))
            {
                return count;
            }
        }

        return -1;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git missing from PATH, or the worktree directory vanished — the same
            // unobservable-git case VerificationRunner.RunGitAsync produces its own (-1, "")
            // sentinel for, so every caller's null-modified-list handling reaches identically.
            return (-1, string.Empty, string.Empty);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await standardOutput, await standardError);
    }
}
