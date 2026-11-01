using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// What a run's worktree and branch hold right now, observed with git and never assumed: the branch ref's
/// tip and the uncommitted paths that would be stranded if the branch were pushed. Shared by the daemon's
/// human-gate check, which counts uncommitted work the way <c>h9k review fixed</c> does from the command
/// line (that command keeps its own listing, written to the same rule), and by the briefing, which reads
/// only the branch tip from here.
/// </summary>
public static class BranchObservation
{
    /// <summary>
    /// The tip of <paramref name="branch"/> read from the branch ref itself rather than from HEAD,
    /// because the pull request opener pushes the branch and a worktree whose HEAD sits elsewhere would
    /// otherwise vouch for commits the push does not carry. Null when git could not be asked.
    /// </summary>
    public static async Task<string?> ReadBranchTipAsync(
        ProcessRunner runner, string worktreePath, string branch, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktreePath) || string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        try
        {
            ProcessResult result = await runner(
                "git", ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], worktreePath, cancellationToken);
            string tip = result.StandardOutput.Trim();
            return result.ExitCode == 0 && tip.Length > 0 ? tip : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The uncommitted paths that block a hand-back, counted exactly as <c>h9k review fixed</c> counts
    /// them: every modified or staged tracked file, plus untracked files under src/ or tests/ that are not
    /// a known build output. An untracked path elsewhere can be a gate byproduct and is left out. Null when
    /// git could not be asked, which a caller must never read as clean.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ListBlockingUncommittedAsync(
        ProcessRunner runner, string worktreePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktreePath))
        {
            return null;
        }

        try
        {
            ProcessResult result = await runner(
                "git", ["status", "--porcelain", "-z", "--untracked-files=all"], worktreePath, cancellationToken);
            if (result.ExitCode != 0)
            {
                return null;
            }

            (IReadOnlyList<string> modified, IReadOnlyList<string> untracked) =
                WorktreeGitStatus.ParsePorcelain(result.StandardOutput);
            (IReadOnlyList<string> strandable, _) = WorktreeGitStatus.SplitUntracked(untracked);
            return [.. modified, .. strandable];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
