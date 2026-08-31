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
    /// file is often a gate byproduct the project's .gitignore has not caught up with, but a
    /// caller must not treat every untracked path as advisory — one under src/ or tests/ is
    /// first-class strandable work that blocks both <c>h9k task deliver</c> and the daemon's own
    /// pre-gate check (<see cref="WorktreeGitStatus.IsUnderSourceOrTestTree"/>; independent pre-PR
    /// review, cycle 1: this comment used to call every untracked path "never blocking", which
    /// stopped being true the moment VerificationRunner started failing the run over exactly this
    /// class of file).
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
    /// The branch the worktree is actually checked out to, or null when git could not be asked
    /// (never guessed at as matching). <c>git branch --show-current</c> rather than
    /// <c>rev-parse --abbrev-ref HEAD</c>: it exits 0 with empty output for a detached HEAD
    /// instead of failing, so a caller can tell "git is unreadable" (null, warn and skip) apart
    /// from "checked out somewhere else, including detached" (empty or a different name, block)
    /// without a second command (conformance review, cycle 4).
    /// </summary>
    public static async Task<string?> GetCurrentBranchAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await RunGitAsync(worktreePath, ["branch", "--show-current"], cancellationToken);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Always --force-with-lease, for the same reason PullRequestOpener's own push carries the
    /// flag: a fresh branch cut by h9k task work has no remote history, so the bare lease is
    /// satisfied trivially and this behaves like a plain first push — but CheckoutFreshOrRetryAsync
    /// resumes a task's RetryBranch (h9k task retry after a failed deliver) or a handed-back
    /// branch, either of which may already carry the history this same push published last time,
    /// now rewritten per the narrative-commit-style fixup/rebase doctrine. A plain push there is
    /// rejected non-fast-forward with no lever left to publish the rebased tree. This no longer
    /// mirrors PullRequestOpener's push exactly, though: that one now runs an ancestor-or-reflog
    /// guard before pinning the lease's expected value explicitly (Decisions Log #104), refusing
    /// outright rather than forcing over a tip it cannot account for, while this one still trusts
    /// the bare flag against whatever this node's own refs/remotes/origin/&lt;branch&gt; last read —
    /// a gap the interactive path has not needed to close yet (a foreign push onto a pre-PR task
    /// branch has no routine source in a single-node install).
    /// </summary>
    public static async Task<(bool Succeeded, string Error)> PushAsync(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        (int exitCode, _, string standardError) = await RunGitAsync(
            worktreePath, ["push", "--force-with-lease", "-u", "origin", branch], cancellationToken);
        if (exitCode == 0)
        {
            return (true, string.Empty);
        }

        // RunGitAsync's own (-1, "", "") sentinel for a git process that never started (the
        // worktree directory vanished from under the claim, or git itself is missing from PATH)
        // is otherwise indistinguishable from a git failure that simply wrote nothing to stderr,
        // and reporting it verbatim left the operator staring at a bare "Push failed: " with no
        // named cause (adversarial review, cycle 4).
        string error = exitCode == -1 && standardError.IsBlank()
            ? $"git could not be run against {worktreePath} — the worktree directory may no longer exist."
            : standardError.Trim();
        return (false, error);
    }

    /// <summary>
    /// <paramref name="workingDirectory"/> and <paramref name="headReference"/> default to the
    /// claim's own worktree and its HEAD, but a caller whose worktree no longer exists on disk
    /// (TaskReleaseCommand's own "worktree gone" case) can point this at the repository itself
    /// with the branch name instead — worktrees share refs with the repository they were cut
    /// from, so the branch's commits are still readable there even once the working directory
    /// that held it is gone (adversarial review, cycle 1, TaskReleaseCommand.cs:129).
    /// </summary>
    public static async Task<int> CountBranchCommitsAsync(
        string workingDirectory, string baseBranch, CancellationToken cancellationToken, string headReference = "HEAD")
    {
        foreach (string baseRef in new[] { $"origin/{baseBranch}", baseBranch })
        {
            (int exitCode, string output, _) = await RunGitAsync(
                workingDirectory, ["rev-list", "--count", $"{baseRef}..{headReference}"], cancellationToken);
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
