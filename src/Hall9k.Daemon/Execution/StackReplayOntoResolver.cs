using Hall9k.Connectors.Processes;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The commit a <c>FollowUpKind.StackReplay</c> follow-up's own prompt names as its
/// <c>--onto</c> boundary (task: a stacked pull-request edge exists as an explicit opt-in
/// dependency). Lives beside <c>RunLauncher</c>, its only caller, but stands alone — taking a
/// <see cref="ProcessRunner"/> directly rather than a live worktree checkout — so this one
/// git-reading decision is testable without the rest of <c>RunLauncher</c>'s own dependency graph.
/// </summary>
public static class StackReplayOntoResolver
{
    /// <summary>
    /// What <see cref="ResolveAsync"/> resolved: the commit to land a stacked replay on, and
    /// whether it came from a fresh read of the base's own current tip rather than the caller's
    /// recorded dispatch-time prediction.
    /// </summary>
    public readonly record struct Resolution(string Commit, bool ResolvedFromCurrentBaseTip);

    /// <summary>
    /// <paramref name="recordedOntoCommit"/> for an ordinary, first dispatch — it was written
    /// moments earlier by whatever dispatched this follow-up (<c>CloseoutEngine</c> or
    /// <c>StackedParentWatch</c>), so it already names the base's own current tip. A retry
    /// (<paramref name="retryPending"/>) reads <paramref name="baseBranch"/>'s own tip fresh from
    /// git instead: <paramref name="retryPending"/> being true means this task has already shown
    /// that prediction can go stale by the time a retry actually runs. Origin incident,
    /// 2026-09-15, task 450b9d84/PR #382: the base took a real Decisions Log number for another
    /// entry between a failed replay lap and its retry, and the retry rebuilt its prompt against
    /// the stale recorded commit, landing short of that number and failing the same numbering
    /// guard a second time.
    /// <para>
    /// Falls back to <paramref name="recordedOntoCommit"/> on any git failure — an unreachable
    /// origin is not a reason to hand the session an empty boundary — and never throws.
    /// </para>
    /// <para>
    /// <c>FollowUpKind.Rebase</c> carries no equivalent hazard and has no resolver of its own
    /// here: its own onto target is always <c>origin/&lt;base&gt;</c>, a ref
    /// <c>AgentPromptBuilder.BuildRebase</c>'s own prompt fetches fresh at session run time, never
    /// a commit frozen at dispatch time the way a replay's boundary is.
    /// </para>
    /// </summary>
    public static async Task<Resolution> ResolveAsync(
        ProcessRunner git, string worktreePath, string baseBranch, string recordedOntoCommit,
        bool retryPending, CancellationToken cancellationToken)
    {
        if (!retryPending)
        {
            return new Resolution(recordedOntoCommit, ResolvedFromCurrentBaseTip: false);
        }

        try
        {
            ProcessResult fetch = await git("git", ["fetch", "origin", baseBranch], worktreePath, cancellationToken);
            if (fetch.ExitCode != 0)
            {
                return new Resolution(recordedOntoCommit, ResolvedFromCurrentBaseTip: false);
            }

            ProcessResult tip = await git(
                "git", ["rev-parse", $"origin/{baseBranch}"], worktreePath, cancellationToken);
            string trimmedTip = tip.StandardOutput.Trim();
            return tip.ExitCode == 0 && trimmedTip.Length > 0
                ? new Resolution(trimmedTip, ResolvedFromCurrentBaseTip: true)
                : new Resolution(recordedOntoCommit, ResolvedFromCurrentBaseTip: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Resolution(recordedOntoCommit, ResolvedFromCurrentBaseTip: false);
        }
    }
}
