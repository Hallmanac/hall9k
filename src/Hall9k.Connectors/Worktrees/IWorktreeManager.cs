using Hall9k.Domain.Features.Project;

namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// Everything the branch a fresh run works on is cut from. <paramref name="BranchNameTemplate"/>
/// is the project's own convention and <paramref name="ExternalReference"/> the task's linked item
/// (<c>jira:PROJ-123</c>, <c>github:owner/repo#42</c>, or null) — both are carried rather than
/// looked up, because the branch name is rendered exactly once, here, and every consumer
/// afterwards reads the name recorded on <c>RunDispatched</c>.
/// </summary>
public sealed record WorktreeRequest(
    string RepositoryPath,
    /// <summary>
    /// The branch this run's work is cut from. The project's own base branch for every ordinary
    /// run; a stacked child's parent branch instead (task: a stacked pull-request edge exists as
    /// an explicit opt-in dependency), which is why this is a caller-resolved value rather than
    /// something read from the project here.
    /// </summary>
    string BaseBranch,
    Guid TaskId,
    Guid RunId,
    string Objective,
    BranchNameTemplate BranchNameTemplate,
    string? ExternalReference);

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

/// <summary>
/// A checkout a run works in. <paramref name="StartPoint"/> is the ref the branch was cut from as
/// this manager named it (<c>origin/main</c>, or the branch itself when it was resumed rather than
/// cut). <paramref name="StartPointCommit"/> is that ref resolved to a commit at the moment of the
/// cut — the branch's fork point, observed when it was true.
/// <para>
/// A commit and not just the ref, because a ref stops naming that point the moment it moves: a
/// stacked child's replay onto a force-pushed parent needs the head the child was actually built
/// on, and <c>git merge-base</c> cannot recover it — a force-push rewrites the shared history, so
/// the merge base collapses back to the base branch and a replay from there re-applies the parent's
/// OLD commit against its new one (verified in a scratch repository: it conflicts on the parent's
/// own content). Empty when nothing was resolved: a resumed branch has no fresh cut to report, and
/// a rev-parse that could not be read is admitted as unknown rather than guessed at (AGENTS.md).
/// </para>
/// </summary>
public sealed record Worktree(string Path, string Branch, string StartPoint, string StartPointCommit = "");

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
    /// Best-effort deletion of the remote-tracking ref <see cref="CreatePrReviewCheckoutAsync"/>
    /// fetched a reviewed pull request's head into (adversarial review, cycle 1): nothing else
    /// ever removes it, so a project used for routine pr-review work would otherwise accumulate
    /// one permanent <c>refs/remotes/origin/pr-review/&lt;n&gt;</c> ref per pull request ever
    /// reviewed, and — worse than the clutter — the accumulation collides with a real branch
    /// literally named <c>pr-review</c> ever appearing on origin, which the ordinary tracking-ref
    /// fetch cannot create alongside a same-named directory of numbered refs. Call only after the
    /// worktree that held the ref detached is already removed.
    /// </summary>
    Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken);

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

    /// <summary>
    /// The same in-process-and-cross-process repository lock every method above already takes
    /// before touching a repository (Decisions Log #4, adversarial review cycle 4), exposed for a
    /// caller that runs git directly against the repository rather than through one of the
    /// operations above — closeout's mechanical rebase fast path, which fetches and rebases in a
    /// retained worktree with no worktree add/remove of its own to route through them. Dispose the
    /// result to release it.
    /// </summary>
    Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>
    /// A narrower lock than <see cref="AcquireRepositoryLockAsync"/>: scoped to exactly
    /// <paramref name="checkoutPath"/>'s own git-dir rather than the repository's shared
    /// git-common-dir, so it serializes command spawns against that one checkout only, never
    /// against <c>git worktree add</c>/<c>remove</c> for every other worktree the repository has
    /// (independent pre-PR review, cycle 1, adversarial lens, medium: a clean-base comparison
    /// budgeted off a gate's own recorded duration can now hold a lock for as long as that gate
    /// takes to run — up to <c>VerifyGateTimeout</c>, not the old fixed five-minute cap — and
    /// <see cref="AcquireRepositoryLockAsync"/> would have held that span against every other
    /// run's own worktree creation and closeout's own worktree removal on the same project, not
    /// just against the checkout the gate actually runs in). For a linked worktree this resolves
    /// to a directory unique to it; for a bare clone or an ordinary, non-worktree checkout it
    /// falls back to the same directory <see cref="AcquireRepositoryLockAsync"/> would use, since
    /// there the two scopes are identical anyway. Every caller that spawns a gate command
    /// directly against one checkout (a clean-base comparison, <c>h9k task verify</c>,
    /// <c>h9k project set --verify</c>) must use this lock rather than
    /// <see cref="AcquireRepositoryLockAsync"/> — all three share the identical checkout, and
    /// mixing lock scopes between them would let two of their gate spawns race the same
    /// checkout's build output unguarded, the exact corruption
    /// <see cref="AcquireRepositoryLockAsync"/> was first reused here to prevent. Dispose the
    /// result to release it.
    /// </summary>
    Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken);
}
