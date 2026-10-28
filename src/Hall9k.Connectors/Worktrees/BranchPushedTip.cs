using System.Globalization;
using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// Where a task branch stands relative to what its pull request already carries, over a
/// <see cref="ProcessRunner"/> so a test scripts git rather than touching a repository. Shared by
/// <c>h9k task deliver</c> and <c>h9k task release</c>, which refuse over work the pull request lacks,
/// and by closeout, which must not delete a merged branch that still holds such work.
/// </summary>
public static class BranchPushedTip
{
    /// <summary>
    /// How many commits <paramref name="branch"/> holds that the pull request does not already carry: the
    /// smaller of the count past the task's recorded pushed tip (when it was recorded for this branch) and
    /// the count past <c>origin/&lt;branch&gt;</c>, since a commit reachable from either is already on the
    /// pull request and neither boundary can undercount. -1 when neither boundary resolves, which a caller
    /// reports and does not guess at.
    /// </summary>
    public static async Task<int> CountBeyondAsync(
        ProcessRunner git, string worktreePath, string branch, string? recordedBranch, string? recordedTip,
        CancellationToken cancellationToken)
    {
        List<string> boundaries = [];
        if (recordedBranch == branch && recordedTip is { Length: > 0 })
        {
            boundaries.Add(recordedTip);
        }

        boundaries.Add($"origin/{branch}");
        int smallest = -1;
        foreach (string boundary in boundaries)
        {
            ProcessResult result = await git(
                "git", ["rev-list", "--count", $"{boundary}..refs/heads/{branch}"], worktreePath, cancellationToken);
            if (result.ExitCode == 0
                && int.TryParse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture, out int count)
                && (smallest < 0 || count < smallest))
            {
                smallest = count;
            }
        }

        return smallest;
    }
}
