namespace Hall9k.Daemon.Worktrees;

public sealed record WorktreeRequest(
    string RepositoryPath,
    string BaseBranch,
    Guid TaskId,
    Guid RunId,
    string Objective);

/// <summary>Follow-up runs resume the task's existing pull-request branch — no new branch is cut.</summary>
public sealed record FollowUpWorktreeRequest(
    string RepositoryPath,
    string Branch,
    Guid TaskId,
    Guid RunId);

public sealed record Worktree(string Path, string Branch, string StartPoint);

public sealed class WorktreeException(string message) : Exception(message);

/// <summary>
/// One worktree per run, siblings of the repository (Decisions Log #4). The executor
/// consumes this seam; nothing here spawns agents.
/// </summary>
public interface IWorktreeManager
{
    Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Puts an EXISTING branch into a usable worktree (follow-up runs on a task's PR
    /// branch). Worktrees are retained through closeout (Decisions Log #21), so the
    /// branch is usually still checked out in the previous run's worktree — that worktree
    /// is reused as-is. Otherwise: a fresh worktree on the local branch, fast-forwarded
    /// to origin when it moved ahead; recreated from origin when only the remote still
    /// has it (the other-node and purged-artifact cases).
    /// </summary>
    Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a worktree (force — done worktrees may hold build debris). The branch survives.</summary>
    Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort deletion of a task branch everywhere it lingers after its pull request
    /// merged: the local branch (git branch -D — PRs land via rebase merge, so the tip is
    /// never an ancestor of the base branch; the merged-PR signal the caller observed is
    /// the justification), the remote branch (when the merge did not already delete it),
    /// and stale remote-tracking refs (git fetch --prune). Call only after the branch's
    /// worktree is removed — a checked-out branch cannot be deleted.
    /// </summary>
    Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken);

    /// <summary>Startup sweep: collect worktree records orphaned by crashes.</summary>
    Task PruneAsync(string repositoryPath, CancellationToken cancellationToken);
}
