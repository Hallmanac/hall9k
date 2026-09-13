namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// Who deletes a merged pull request's head branch from origin — an unpersisted in-process
/// decision, so an enum is right here (TASK-MODEL.md §8).
/// </summary>
public enum RemoteBranchDeletionOwner
{
    /// <summary>
    /// This platform pushes the deletion itself, which is what closeout has always done and what
    /// it still does wherever the repository's own setting reads false or could not be read.
    /// </summary>
    Daemon,

    /// <summary>
    /// GitHub deletes it, because the repository has automatic head-branch deletion turned on
    /// (<c>delete_branch_on_merge</c>), and only GitHub's OWN deletion retargets the open pull
    /// requests stacked on that branch — a raw <c>git push --delete</c> closes them instead
    /// (Decisions Log #186).
    /// </summary>
    GitHub,
}

/// <summary>
/// Which git call one step of the cleanup is, so the caller executing the plan can give each its
/// own reading of a nonzero exit without re-deriving the sequence. Unpersisted, in-process, so an
/// enum for the same reason <see cref="RemoteBranchDeletionOwner"/> is one.
/// </summary>
public enum MergedBranchCleanupStage
{
    /// <summary>
    /// <c>git branch -D</c>. Forced, not <c>-d</c>: pull requests land via rebase merge, so the
    /// branch tip is never an ancestor of the base branch, and the caller's merged-PR observation
    /// is the justification.
    /// </summary>
    DeleteLocalBranch,

    /// <summary>
    /// <c>git remote get-url origin</c> — the probe that tells a repository with a remote from one
    /// without. A nonzero exit ends the plan: every step after it is about origin.
    /// </summary>
    ReadOriginUrl,

    /// <summary>
    /// <c>git push origin --delete</c>. Present only when the deletion is
    /// <see cref="RemoteBranchDeletionOwner.Daemon"/>'s to make.
    /// </summary>
    DeleteRemoteBranch,

    /// <summary>
    /// <c>git fetch --prune origin</c>, which drops the stale remote-tracking ref. Runs under
    /// either owner: the branch is gone from origin whoever deleted it.
    /// </summary>
    PruneRemoteRefs,
}

/// <summary>One git invocation in the cleanup, as the argument string the runner is handed.</summary>
public sealed record MergedBranchCleanupStep(MergedBranchCleanupStage Stage, string Arguments);

/// <summary>
/// The git sequence <see cref="IWorktreeManager.DeleteBranchEverywhereAsync"/> runs to clean up a
/// merged run's branch, decided rather than executed — a pure function of the branch name and who
/// owns the deletion on origin, so the decision is readable (and testable) without a repository,
/// a branch, or GitHub.
/// <para>
/// It exists because of one asymmetry in GitHub's behaviour, found the hard way (origin incident
/// 2026-09-13 02:07 EDT, the first stacked pair on this board): GitHub retargets the open pull
/// requests stacked on a merged branch only when GitHub ITSELF deletes that branch — the delete
/// button after a merge, or the repository's own automatic head-branch deletion. A raw ref
/// deletion, which is what <c>git push origin --delete</c> and the REST delete-ref both are,
/// CLOSES those children instead, with <c>base_ref_deleted</c> on the timeline and no
/// <c>automatic_base_change_succeeded</c> anywhere. PR #338 was closed that way six hours after
/// its parent merged. So where the repository deletes head branches on merge, the honest thing is
/// to leave the remote deletion alone: GitHub has already made it, or is about to, and its
/// deletion is the one that carries the children with it.
/// </para>
/// </summary>
public static class MergedBranchCleanup
{
    /// <summary>
    /// Who owns the remote deletion, from the repository's own <c>delete_branch_on_merge</c>
    /// setting as it was read — <see langword="null"/> for a read that could not be made at all.
    /// Only an observed <see langword="true"/> hands the deletion to GitHub: an unread setting is
    /// not evidence the repository has one turned on (AGENTS.md's never-guess rule), and today's
    /// behaviour — the platform deleting it itself — is what an unknown falls back to, since that
    /// is the behaviour every repository got before this decision existed.
    /// </summary>
    public static RemoteBranchDeletionOwner OwnerOf(bool? repositoryDeletesHeadBranchOnMerge) =>
        repositoryDeletesHeadBranchOnMerge == true
            ? RemoteBranchDeletionOwner.GitHub
            : RemoteBranchDeletionOwner.Daemon;

    /// <summary>
    /// The ordered sequence for <paramref name="branch"/>. The local deletion and the prune are
    /// unconditional; only <see cref="MergedBranchCleanupStage.DeleteRemoteBranch"/> turns on
    /// <paramref name="remoteDeletion"/>.
    /// </summary>
    public static IReadOnlyList<MergedBranchCleanupStep> Plan(
        string branch, RemoteBranchDeletionOwner remoteDeletion)
    {
        List<MergedBranchCleanupStep> plan =
        [
            new(MergedBranchCleanupStage.DeleteLocalBranch, $"branch -D \"{branch}\""),
            new(MergedBranchCleanupStage.ReadOriginUrl, "remote get-url origin"),
        ];

        if (remoteDeletion == RemoteBranchDeletionOwner.Daemon)
        {
            plan.Add(new MergedBranchCleanupStep(
                MergedBranchCleanupStage.DeleteRemoteBranch, $"push origin --delete \"{branch}\""));
        }

        plan.Add(new MergedBranchCleanupStep(MergedBranchCleanupStage.PruneRemoteRefs, "fetch --prune origin"));
        return plan;
    }
}
