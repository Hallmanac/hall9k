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

/// <summary>
/// A pr-review task's read-only target: someone else's pull request, fetched fresh at every
/// dispatch (retry included) rather than resumed — nothing is ever committed into it, so
/// there is nothing to preserve between attempts the way a follow-up's own branch is.
/// </summary>
public sealed record PrReviewWorktreeRequest(
    string RepositoryPath,
    int PullRequestNumber,
    Guid TaskId,
    Guid RunId);

public sealed record Worktree(string Path, string Branch, string StartPoint);

/// <summary>
/// What a refresh of a long-lived reading checkout actually managed. <paramref name="Detail"/>
/// is a sentence for a log line, and it never claims more than was observed: an unreachable
/// remote or a checkout that could not be fast-forwarded says so, rather than being folded into
/// silence that reads as "up to date".
/// </summary>
public sealed record CheckoutRefresh(bool UpToDate, string Detail);

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

    /// <summary>
    /// A read-only, detached checkout of a pull request's head commit (<c>refs/pull/&lt;n&gt;/head</c>,
    /// which GitHub exposes on the base repository regardless of whether the pull request is
    /// from a fork — no second remote needed either way). No local branch is created, so there
    /// is nothing here a session could accidentally push.
    /// </summary>
    Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken);

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

    /// <summary>
    /// Best-effort fast-forward of a checkout a session is about to READ, rather than one it is
    /// about to work in: the home's <c>repo/dev</c>, cut once by <c>h9k project init</c> and
    /// otherwise never touched. A session is spawned there to read the project's own rules, so a
    /// checkout months behind the remote answers with rules nobody uses any more.
    /// <para>
    /// Fast-forward only, and never throws: a reading session running against slightly old code
    /// is worth far more than one refused because the remote was unreachable. What it did or
    /// could not do comes back in the result so the caller can say it out loud, which is the
    /// half that actually matters — the defect this exists for was a stale checkout that nothing
    /// reported (Decisions Log #76).
    /// </para>
    /// <para>
    /// The repository to fetch is resolved from the checkout itself rather than taken as an
    /// argument, because the two can disagree and only one of them is the one the checkout reads:
    /// <c>repo/dev</c> is a worktree of the home's bare clone, while the project's recorded
    /// repository path may still name somewhere else entirely (<c>--keep-repo-path</c>,
    /// <c>h9k project set --repo</c>). A caller cannot pair them wrongly if it never pairs them.
    /// </para>
    /// </summary>
    Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
        string checkoutPath, string branch, CancellationToken cancellationToken);
}
