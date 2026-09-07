using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// What one look at a stacked child's parent found — an unpersisted in-process outcome, so an enum
/// is right here (TASK-MODEL.md §8).
/// </summary>
public enum StackedParentVerdict
{
    /// <summary>Nothing to do: this run is not a stacked child, or its parent's head is still what the child is built on.</summary>
    Aligned,

    /// <summary>
    /// The parent's pull request merged. The child's own pull request needs retargeting onto the
    /// project's base branch and its commits replaying there.
    /// </summary>
    ParentMerged,

    /// <summary>
    /// The parent's branch head moved without merging — a force-push, ordinarily a review lap
    /// folding fixes into its own commits. The base stays the parent's branch; only the replay is
    /// owed.
    /// </summary>
    ParentMoved,

    /// <summary>
    /// Something could not be read, so no claim is made either way. Never treated as Aligned: a
    /// failed git call is not evidence the branches agree (AGENTS.md's never-guess rule). The next
    /// sweep looks again.
    /// </summary>
    Unobservable,
}

/// <summary>
/// One observation of a stacked child against its parent.
/// </summary>
/// <param name="BoundaryCommit">
/// The commit everything at or before which belongs to the parent — the <c>&lt;upstream&gt;</c> of
/// the replay's own <c>git rebase --onto</c>, which is what drops the parent's commits instead of
/// replaying them onto a base that already holds them. The parent's own head where that head is
/// still on the child's line (a merged parent that was never rewritten), and otherwise the child
/// run's own recorded fork point (<c>RunDetails.BaseCommit</c>), once the child's branch is
/// confirmed to contain it — never <c>git merge-base</c>: see
/// <see cref="StackedParentWatch"/>'s own doc for the force-push case that proves merge-base wrong
/// here. Blank on <see cref="StackedParentVerdict.Aligned"/> and
/// <see cref="StackedParentVerdict.Unobservable"/>, where there is no replay to describe.
/// </param>
/// <param name="OntoCommit">
/// The commit the replay lands on, freshly observed: the parent's new head for a force-push, or the
/// project's base branch tip once the parent merged. A commit rather than a ref, for the reasons
/// <c>TaskReopened.StackReplayOntoCommit</c> gives. Blank alongside
/// <paramref name="BoundaryCommit"/>.
/// </param>
/// <param name="Detail">What was observed, in a sentence for the log and the reopen's reason.</param>
public sealed record StackedParentObservation(
    StackedParentVerdict Verdict,
    string ParentBranch,
    string BoundaryCommit,
    string OntoCommit,
    string Detail)
{
    public static StackedParentObservation Aligned(string detail) =>
        new(StackedParentVerdict.Aligned, string.Empty, string.Empty, string.Empty, detail);

    public static StackedParentObservation Unobservable(string detail) =>
        new(StackedParentVerdict.Unobservable, string.Empty, string.Empty, string.Empty, detail);
}

/// <summary>
/// Watches the parent branch a stacked child's pull request is built on and targeted at (task: a
/// stacked pull-request edge exists as an explicit opt-in dependency), and answers the one question
/// closeout asks each sweep: has that branch moved out from under this child?
/// <para>
/// Everything here is read from git and from the parent's own recorded state — never inferred from
/// elapsed time or from the child's own age. The boundary the replay drops the parent's commits at
/// is observed directly wherever git can still answer it — the parent's head, where that head is
/// still contained in the child's branch — and falls back to the child run's own recorded fork
/// point (<c>RunDetails.BaseCommit</c>) where it cannot — which is the force-pushed case, and which
/// is trusted only once git confirms the child's branch actually contains that commit, because for a
/// replay run the field is a dispatch-time prediction rather than an observation (see
/// <see cref="ObserveAsync"/>'s own containment check). What it is
/// never computed from is <c>git merge-base</c>, because that gets it
/// wrong in exactly the case this watch exists for. A force-pushed parent rewrites the history the
/// child shares with it, so the merge base of the child and the parent's NEW head collapses back to
/// the base branch — and a replay from there re-applies the child's copy of the parent's OLD commit
/// against the parent's new one, which conflicts on the parent's own content. Verified in a scratch
/// repository while this was being built, which is the only reason it is not still written the
/// obvious way. A child whose parent moved without merging and whose run recorded no fork point
/// either (a stream written before that field) is therefore
/// <see cref="StackedParentVerdict.Unobservable"/> rather than replayed on a guess.
/// </para>
/// <para>
/// Runs against the project's bare repository rather than the child run's retained worktree,
/// deliberately: the worktree can be gone (removed at closeout, purged by hand, or never present on
/// this node at all), while the bare clone holds every ref this needs and is where a fetch belongs.
/// Every call is made under <see cref="IWorktreeManager.AcquireRepositoryLockAsync"/>, the same lock
/// every other direct-git path in the daemon takes (Decisions Log #4).
/// </para>
/// </summary>
public sealed class StackedParentWatch(
    IWorktreeManager worktrees,
    ILogger<StackedParentWatch> logger)
{
    /// <summary>
    /// Sized like the closeout engine's own git deadline rather than the default process deadline:
    /// the fetch below transfers real data, and the plain runner would kill it mid-transfer on a
    /// large repository or a slow uplink (the same reasoning <c>CloseoutEngine.GitDeadline</c> and
    /// <c>PullRequestOpener.PushDeadline</c> both state).
    /// </summary>
    private static readonly TimeSpan GitDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The temporary ref a parent's pull-request head is fetched into when its branch is gone from
    /// origin — namespaced under <c>refs/hall9k/</c> so it can never collide with a real branch,
    /// and deleted in this method's own finally rather than left to accumulate (the clutter
    /// <see cref="IWorktreeManager.DeletePrReviewTrackingRefAsync"/> exists to prevent).
    /// </summary>
    private static string ParentHeadRef(int pullRequestNumber) => $"refs/hall9k/stacked-parent/{pullRequestNumber}";

    /// <summary>
    /// Whether <paramref name="run"/>'s pull request still targets somewhere other than the
    /// project's own base branch — the cheap, local test that tells a stacked child worth watching
    /// from every ordinary run, with no git or provider call at all. Every caller checks this before
    /// paying for <see cref="ObserveAsync"/>.
    /// </summary>
    public static bool IsStackedChild(RunDetails run, ProjectDetails project) =>
        run.AwaitsStackedRetarget(project.BaseBranch);

    /// <summary>
    /// Looks at <paramref name="childRun"/>'s parent and reports whether the child is still built
    /// on the parent's current head. <paramref name="childTask"/>'s declared stacked edge names the
    /// parent; the parent's own <c>TaskListItem</c> and current run supply its state and pull
    /// request.
    /// </summary>
    public async Task<StackedParentObservation> ObserveAsync(
        IQuerySession query,
        ProjectDetails project,
        TaskAggregate childTask,
        RunDetails childRun,
        CancellationToken cancellationToken)
    {
        if (childTask.StackedOnTaskId is not { } parentId)
        {
            // The run records a non-project base but the task declares no edge. Not a
            // contradiction to resolve here: a human can retarget a pull request on GitHub by hand
            // for reasons this platform knows nothing about, and the honest answer is that there is
            // no parent to watch — not that the two disagree.
            return StackedParentObservation.Aligned(
                "the pull request targets a branch other than the project's base, but this task declares no "
                + "stacked edge — nothing here is watching a parent");
        }

        TaskListItem? parent = await query.LoadAsync<TaskListItem>(parentId, cancellationToken);
        RunDetails? parentRun = parent?.CurrentRunId is { } parentRunId
            ? await query.LoadAsync<RunDetails>(parentRunId, cancellationToken)
            : null;
        if (parent is null)
        {
            return StackedParentObservation.Unobservable(
                $"the parent task {parentId} is no longer in the platform's records, so its branch cannot be read");
        }

        string parentBranch = childRun.BaseBranch;
        bool parentMerged = parent.State == TaskState.Done && parentRun?.State == RunState.Completed;

        ProcessRunner git = ExternalProcess.RunnerWithDeadline(GitDeadline);
        string repositoryPath = project.RepositoryPath;
        await using IAsyncDisposable repositoryLock =
            await worktrees.AcquireRepositoryLockAsync(repositoryPath, cancellationToken);

        string? fetchedRef = null;
        try
        {
            // The parent branch's current head, as a ref this repository can name. Origin's own
            // branch is preferred: after a rebase merge it still points at the parent's pre-merge
            // tip, which is exactly the boundary, and it costs one ordinary fetch. Only when that
            // branch is gone from origin — the parent's closeout deletes it — is the pull request's
            // own immutable head ref fetched instead, which GitHub keeps forever.
            string? parentHead = await TryResolveRemoteBranchAsync(
                git, repositoryPath, parentBranch, cancellationToken);
            if (parentHead is null && parentRun?.PullRequestNumber is > 0)
            {
                fetchedRef = ParentHeadRef(parentRun.PullRequestNumber.Value);
                parentHead = await TryFetchPullRequestHeadAsync(
                    git, repositoryPath, parentRun.PullRequestNumber.Value, fetchedRef, cancellationToken);
                if (parentHead is null)
                {
                    fetchedRef = null;
                }
            }

            if (parentHead is null)
            {
                return StackedParentObservation.Unobservable(
                    $"neither origin/{parentBranch} nor the parent's own pull-request head could be resolved, so "
                    + "whether the parent's branch has moved is unobserved rather than assumed");
            }

            ProcessResult isAncestor = await git(
                "git",
                ["merge-base", "--is-ancestor", parentHead, $"refs/heads/{childRun.Branch}"],
                repositoryPath,
                cancellationToken);

            // Exit 0 means the parent's head is already contained in the child's branch. Exit 1
            // means it is not. Anything else is git failing to answer, which is not the same as
            // either (git documents exactly this three-way reading of --is-ancestor).
            if (isAncestor.ExitCode is not (0 or 1))
            {
                return StackedParentObservation.Unobservable(
                    $"git could not tell whether {parentBranch}'s head is contained in {childRun.Branch}: "
                    + FirstLine(isAncestor.StandardError));
            }

            bool childHoldsParentHead = isAncestor.ExitCode == 0;

            // Aligned is answered before the boundary is ever needed: a child still built on its
            // parent's current head owes nothing, whether or not its run recorded a fork point.
            // Only a merged parent breaks that, because its branch is going away regardless of how
            // aligned the two are — the pull request has to move off it.
            if (childHoldsParentHead && !parentMerged)
            {
                return StackedParentObservation.Aligned(
                    $"still built on {parentBranch}'s current head — nothing to replay");
            }

            // The boundary. Directly observed wherever it can be: a child that CONTAINS the
            // parent's current head — which only reaches here when the parent merged — was not
            // rewritten out from under, and holding the parent's whole branch means that head IS
            // the highest parent commit on the child's own line. Preferred over the record even
            // when there is one (independent pre-PR review, cycle 1, adversarial lens): the record
            // is this run's fork point as it was at dispatch, and a rebase that moves the branch
            // afterwards leaves it naming a commit the branch may no longer contain — a replay
            // from a stale upstream re-applies the parent's commits onto the base instead of
            // dropping them, the exact duplication this boundary exists to prevent. An observed
            // commit that is currently true beats a recorded one that was.
            //
            // Only a parent that moved WITHOUT merging falls back to the record, and it has to:
            // the parent's head is no longer on the child's line at all there, so git has nothing
            // left to read the boundary from — merge-base gets it wrong for exactly this case
            // (this type's own doc). A child whose run recorded no fork point either is admitted
            // as unobservable rather than replayed on a guess.
            string boundary = childHoldsParentHead
                ? parentHead
                : childRun.BaseCommit;
            if (boundary.IsBlank())
            {
                return StackedParentObservation.Unobservable(
                    $"{parentBranch} has moved, but this run recorded no fork point of its own, so the commit a "
                    + "replay would drop the parent's work at is unobserved rather than assumed");
            }

            // The record is checked against the branch before it is trusted (adversarial review,
            // cycle 4). For a StackReplay run that field is a dispatch-time PREDICTION rather than
            // an observation — RunLauncher records the commit the replay was told to land on as the
            // new run's BaseCommit before the session has performed the rebase — so a replay that
            // ends without landing (its own prompt sanctions `git rebase --abort` on a conflict it
            // cannot honestly resolve, and the no-op push then succeeds) leaves a recorded fork
            // point the branch never reached. Replaying from it would hand the next session a range
            // that still contains the parent's own commits and tell it those are this task's work —
            // the exact duplication this boundary exists to prevent, in a mechanical session no
            // reviewer reads. Unobservable is the honest answer; the next sweep asks again, and a
            // replay that does land makes the record true.
            if (!childHoldsParentHead)
            {
                ProcessResult boundaryHeld = await git(
                    "git",
                    ["merge-base", "--is-ancestor", boundary, $"refs/heads/{childRun.Branch}"],
                    repositoryPath,
                    cancellationToken);
                if (boundaryHeld.ExitCode != 0)
                {
                    return StackedParentObservation.Unobservable(
                        boundaryHeld.ExitCode == 1
                            ? $"{parentBranch} has moved, but {childRun.Branch} does not contain the fork point "
                              + $"{Short(boundary)} this run recorded — a replay from a commit the branch never "
                              + "landed on would carry the parent's own work as this task's, so the boundary is "
                              + "unobserved rather than assumed"
                            : $"git could not tell whether {childRun.Branch} contains the fork point "
                              + $"{Short(boundary)} this run recorded: " + FirstLine(boundaryHeld.StandardError));
                }
            }
            if (parentMerged)
            {
                // Onto the project's base branch's own freshly observed tip, which is where the
                // retarget is about to aim the pull request.
                string? baseTip = await TryResolveRemoteBranchAsync(
                    git, repositoryPath, project.BaseBranch, cancellationToken);
                if (baseTip is null)
                {
                    return StackedParentObservation.Unobservable(
                        $"the parent's pull request merged, but origin/{project.BaseBranch}'s own tip could not be "
                        + "resolved, so there is no observed commit for the replay to land on");
                }

                return new StackedParentObservation(
                    StackedParentVerdict.ParentMerged, parentBranch, boundary, baseTip,
                    $"the parent task's pull request merged, so this pull request's base moves from "
                    + $"{parentBranch} to {project.BaseBranch} and its own commits replay from {Short(boundary)} "
                    + $"onto {Short(baseTip)}");
            }

            return new StackedParentObservation(
                StackedParentVerdict.ParentMoved, parentBranch, boundary, parentHead,
                $"the parent branch {parentBranch} was force-pushed past what this branch was built on, so this "
                + $"branch's own commits replay from {Short(boundary)} onto its new head {Short(parentHead)}");
        }
        catch (TimeoutException exception)
        {
            // The same stance every other direct-git path in closeout takes on a deadline: this
            // sweep learned nothing, and the next one asks again. Letting it escape would skip the
            // outcome the caller records and abandon the rest of this run's inspection.
            logger.LogWarning(
                exception,
                "Run {RunId}: a git call exceeded its deadline observing the stacked parent branch {ParentBranch}",
                childRun.Id, parentBranch);
            return StackedParentObservation.Unobservable(
                $"a git call exceeded its deadline reading {parentBranch}, so nothing was observed this sweep");
        }
        finally
        {
            if (fetchedRef is not null)
            {
                await DeleteRefBestEffortAsync(git, repositoryPath, fetchedRef, cancellationToken);
            }
        }
    }

    /// <summary>
    /// <paramref name="branch"/>'s tip on origin, freshly fetched, or null when origin has no such
    /// branch. A fetch failure and a missing branch are both null here on purpose: the caller's next
    /// step (the pull request's own head ref) covers either, and distinguishing them would only
    /// change which sentence an unobservable outcome carries.
    /// </summary>
    private static async Task<string?> TryResolveRemoteBranchAsync(
        ProcessRunner git, string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        ProcessResult fetch = await git("git", ["fetch", "origin", branch], repositoryPath, cancellationToken);
        if (fetch.ExitCode != 0)
        {
            return null;
        }

        ProcessResult head = await git(
            "git", ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}^{{commit}}"],
            repositoryPath, cancellationToken);
        return head.ExitCode == 0 && head.StandardOutput.Trim().IsNotBlank() ? head.StandardOutput.Trim() : null;
    }

    /// <summary>
    /// The parent pull request's own head commit, fetched into <paramref name="destinationRef"/>
    /// from <c>refs/pull/&lt;n&gt;/head</c> — the ref GitHub keeps after the branch itself is
    /// deleted, which is the whole reason this fallback exists (the same ref
    /// <c>CreatePrReviewCheckoutAsync</c> already relies on).
    /// </summary>
    private static async Task<string?> TryFetchPullRequestHeadAsync(
        ProcessRunner git, string repositoryPath, int pullRequestNumber, string destinationRef,
        CancellationToken cancellationToken)
    {
        ProcessResult fetch = await git(
            "git",
            ["fetch", "--force", "origin", $"refs/pull/{pullRequestNumber}/head:{destinationRef}"],
            repositoryPath,
            cancellationToken);
        if (fetch.ExitCode != 0)
        {
            return null;
        }

        ProcessResult head = await git(
            "git", ["rev-parse", "--verify", "--quiet", $"{destinationRef}^{{commit}}"],
            repositoryPath, cancellationToken);
        return head.ExitCode == 0 && head.StandardOutput.Trim().IsNotBlank() ? head.StandardOutput.Trim() : null;
    }

    private async Task DeleteRefBestEffortAsync(
        ProcessRunner git, string repositoryPath, string reference, CancellationToken cancellationToken)
    {
        try
        {
            await git("git", ["update-ref", "-d", reference], repositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(
                exception, "Could not delete the temporary ref {Ref} in {Repository}", reference, repositoryPath);
        }
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    private static string FirstLine(string? text) =>
        text.IsBlank() ? "no output" : text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
}
