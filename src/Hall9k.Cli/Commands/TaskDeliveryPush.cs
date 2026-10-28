using System.Globalization;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The git half of <c>h9k task deliver</c> and of <c>h9k task release</c> on a claim taken at the PR
/// stage, over a <see cref="ProcessRunner"/> so a test scripts git rather than touching a repository.
/// <para>
/// The push goes through <see cref="ForceWithLeasePusher"/>, the same ancestor-or-reflog-or-recorded
/// guard the pull request opener, closeout's mechanical rebase, and the spike engine call, with the
/// task's recorded pushed tip as its third door exactly as the opener passes it. The bare
/// <c>--force-with-lease</c> this command used to run trusted whatever this node's own
/// <c>refs/remotes/origin</c> last read, so a branch someone else had moved on origin could be
/// overwritten; the guard refuses instead, and says which tip it did not recognise.
/// </para>
/// </summary>
internal static class TaskDeliveryPush
{
    /// <summary>
    /// How a push ended. <see cref="Refusal"/> is a push the guard declined, or git failing, with the reason;
    /// <see cref="Pushed"/> carries the tip the push landed at, read back from the branch ref.
    /// </summary>
    public sealed record Outcome(bool Pushed, string? Refusal, string? PushedTip, bool GuardRefused);

    /// <summary>
    /// Pushes <paramref name="branch"/>. <paramref name="recordedBranch"/> and <paramref name="recordedTip"/>
    /// are the task's <c>LastPushedBranch</c> and <c>LastPushedBranchTip</c>; the tip is handed to the guard
    /// only when the branch is the one it was recorded for, as the opener does.
    /// </summary>
    public static async Task<Outcome> PushAsync(
        ProcessRunner git, string worktreePath, string branch, string? recordedBranch, string? recordedTip,
        CancellationToken cancellationToken)
    {
        HashSet<string> recordedPushedTips = recordedBranch == branch && recordedTip is { Length: > 0 }
            ? [recordedTip]
            : [];
        try
        {
            await ForceWithLeasePusher.PushAsync(git, worktreePath, branch, recordedPushedTips, cancellationToken);
        }
        catch (ForcePushRefusedException exception)
        {
            // The guard's own sentence names the tip it did not recognise; this says what to do about it.
            return new Outcome(
                false,
                $"{exception.Message} Fetch the branch into this worktree and bring your commits onto it, or ask "
                + "whoever moved it, then deliver again.",
                null,
                GuardRefused: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return new Outcome(false, exception.Message, null, GuardRefused: false);
        }

        // The local branch ref, not HEAD, for the reason the opener gives: the push sent refs/heads/<branch>.
        ProcessResult tip = await git(
            "git", ["rev-parse", $"refs/heads/{branch}"], worktreePath, cancellationToken);
        return new Outcome(true, null, tip.ExitCode == 0 && tip.StandardOutput.Trim().Length > 0 ? tip.StandardOutput.Trim() : null, false);
    }

    /// <summary>
    /// How many commits <paramref name="branch"/> holds that the pull request does not already carry: the
    /// smaller of the count past the task's recorded pushed tip (when it was recorded for this branch) and
    /// the count past <c>origin/&lt;branch&gt;</c>, since a commit reachable from either is already on the
    /// pull request and neither boundary can undercount. -1 when neither boundary resolves, which a caller
    /// reports and does not guess at.
    /// </summary>
    public static async Task<int> CountBeyondPushedTipAsync(
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
