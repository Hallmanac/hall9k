using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
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
    /// The parent's pull request merged into the project's own base branch. The child's own pull
    /// request needs retargeting onto that branch and its commits replaying there.
    /// </summary>
    ParentMerged,

    /// <summary>
    /// The parent's branch head moved without merging, to a commit the child's branch does not
    /// contain: ordinarily a review lap folding fixes into its own commits and force-pushing, but
    /// an ordinary commit appended since the child was cut reads identically here and is not
    /// distinguished — nothing observable tells the two apart. The base stays the parent's branch;
    /// only the replay is owed, and it is the same replay either way.
    /// </summary>
    ParentMoved,

    /// <summary>
    /// The parent's pull request merged, but into a branch other than the project's own — the
    /// parent was itself stacked and something merged it while it was still aimed at ITS parent's
    /// branch. Mechanically retargeting the child onto the project's base from here would move it
    /// off a base holding work the project's base does not have; ordering a stack three levels deep
    /// is not in this slice (docs/scope.md), so the child parks for a human instead.
    /// </summary>
    ParentMergedElsewhere,

    /// <summary>
    /// The parent can no longer reach even <em>Delivered</em>, so the base this child is built on
    /// is never going anywhere: the parent task was abandoned, ended Failed, or reads Done having
    /// never delivered a pull request that can merge (its own closed unmerged, or it never opened
    /// one). The one rule for this lives on <c>TaskDependency.IsDeadForStackedChild</c>, asked
    /// through <see cref="StackedEdgeRules"/> — the same seam assignment and the dependency
    /// resolver ask, so a dead parent can never mean one thing before the child dispatches and
    /// another after. A child here parks for a human rather than building on a dead base (task: a
    /// stacked child absorbs its parent's post-delivery churn safely).
    /// </summary>
    ParentDead,

    /// <summary>
    /// The parent's branch is not on origin and no head for it could be resolved from task state
    /// either — not even from the parent's own pull request, which is the ref GitHub keeps after a
    /// branch is deleted. Deliberately distinct from <see cref="Unobservable"/>, which is a read
    /// that FAILED: nothing here failed, every look answered, and what they answered is that there
    /// is no such ref — so no later sweep finds a head this one missed. What that does NOT mean is
    /// permanence: one of the two shapes producing it is a parent branch never pushed yet (a child
    /// claimed ahead of its parent with <c>--acknowledge-unmet-dependencies</c>), which resolves
    /// itself the moment the parent delivers, and the checkpoint's own park says so rather than
    /// telling a human to unstack a branch whose parent is about to push (independent pre-PR
    /// review, cycle 1, adversarial lens). The other shape — a parent whose records carry no pull
    /// request to fall back on — needs the human either way. Closeout treats this verdict as it
    /// treats an unobservable sweep — the child's pull request is open and its own inspection still
    /// owes its answers — while a checkpoint rebase, which has no later sweep to defer to before
    /// the final pass runs, parks with the situation named.
    /// </summary>
    ParentUnresolvable,

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
/// here. Blank on every verdict but <see cref="StackedParentVerdict.ParentMerged"/> and
/// <see cref="StackedParentVerdict.ParentMoved"/> — <see cref="StackedParentVerdict.Aligned"/>,
/// <see cref="StackedParentVerdict.Unobservable"/>,
/// <see cref="StackedParentVerdict.ParentMergedElsewhere"/>,
/// <see cref="StackedParentVerdict.ParentDead"/> and
/// <see cref="StackedParentVerdict.ParentUnresolvable"/> all have no replay to describe.
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

    /// <summary>
    /// Carries the parent's branch, unlike the two above: the park this verdict produces names the
    /// stack the human has to finish by hand, and that branch is half of it.
    /// </summary>
    public static StackedParentObservation ParentMergedElsewhere(string parentBranch, string detail) =>
        new(StackedParentVerdict.ParentMergedElsewhere, parentBranch, string.Empty, string.Empty, detail);

    /// <summary>Carries the parent's branch for the reason <see cref="ParentMergedElsewhere"/> gives.</summary>
    public static StackedParentObservation ParentDead(string parentBranch, string detail) =>
        new(StackedParentVerdict.ParentDead, parentBranch, string.Empty, string.Empty, detail);

    /// <summary>Carries the parent's branch for the reason <see cref="ParentMergedElsewhere"/> gives.</summary>
    public static StackedParentObservation ParentUnresolvable(string parentBranch, string detail) =>
        new(StackedParentVerdict.ParentUnresolvable, parentBranch, string.Empty, string.Empty, detail);
}

/// <summary>
/// Watches the parent branch a stacked child's pull request is built on and targeted at (task: a
/// stacked pull-request edge exists as an explicit opt-in dependency), and answers the one question
/// its two readers ask: has that branch moved out from under this child, and is it still a branch
/// worth following? Closeout asks once per sweep about a child whose pull request is already open;
/// the review loop asks at a child's own two rebase checkpoints, before it has one (task: a stacked
/// child absorbs its parent's post-delivery churn safely). One observation, two callers, so the two
/// can never read the same parent differently.
/// <para>
/// Everything here is read from git and from the parent's own recorded state — never inferred from
/// elapsed time or from the child's own age. The boundary the replay drops the parent's commits at
/// is observed directly wherever git can still answer it — the parent's head, where that head is
/// still contained in the child's branch — and falls back to the child run's own recorded fork
/// point (<c>RunDetails.BaseCommit</c>) where it cannot — which is the parent that moved without
/// merging, the force-pushed lap among them — and which
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
    /// on the parent's current head. <paramref name="childStackedOnTaskId"/> is the child's own
    /// declared stacked edge — <c>TaskAggregate.StackedOnTaskId</c> for closeout, which holds the
    /// aggregate, or <c>TaskDetails.StackedOnTaskId</c> for the review loop's own checkpoints,
    /// which holds the projection — and the parent's own <c>TaskListItem</c> and current run supply
    /// its state and pull request.
    /// </summary>
    public async Task<StackedParentObservation> ObserveAsync(
        IQuerySession query,
        ProjectDetails project,
        Guid? childStackedOnTaskId,
        RunDetails childRun,
        CancellationToken cancellationToken)
    {
        if (childStackedOnTaskId is not { } parentId)
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

        // Asked first, and off task state alone: a parent that can no longer reach even Delivered
        // has a branch that is going nowhere, so whether it moved since the cut is beside the point
        // (task: a stacked child absorbs its parent's post-delivery churn safely). Routed through
        // StackedEdgeRules rather than re-enumerated here, which is the whole reason that type
        // exists — TaskDecider.Assign and TaskDependencyResolver ask the identical question of the
        // identical rule, and a third reading of "dead parent" invented here is exactly how the two
        // halves of this edge would come to disagree. Its snapshot is loaded through
        // TaskDependencyQuery for the same reason: that is the one production producer of a
        // TaskDependency, and mapping a TaskListItem plus a RunDetails into one by hand here would
        // be a second mapping to drift.
        IReadOnlyList<TaskDependency> parentSnapshot =
            await TaskDependencyQuery.LoadAsync(query, [parentId], cancellationToken);
        if (parentSnapshot.FirstOrDefault() is { } parentDependency
            && StackedEdgeRules.IsDead(parentId, parentDependency))
        {
            return StackedParentObservation.ParentDead(
                parentBranch,
                $"the parent task {parentDependency.Describe()} can no longer reach even Delivered — "
                + $"{DescribeParentDeath(parentDependency)} — so {parentBranch} is a base nothing further "
                + "arrives on");
        }

        // Where the parent's own work actually landed, read off the parent's run rather than assumed
        // to be the project's base (independent pre-PR review, 2026-09-07, adversarial lens). They
        // differ in one shape: a mid-stack parent, itself stacked, merged while still aimed at ITS
        // parent's branch. Refused here, before any git call and before anything is retargeted — a
        // retarget onto the project's base would take this child off a base carrying the
        // grandparent's work and replay its commits without it, and this platform's own merge bar
        // never merges an un-retargeted stacked pull request, so nothing automatic produced this
        // state and nothing automatic should answer it. Ordering across three or more levels is out
        // of this slice by design (docs/scope.md), and a park leaves the human a coherent stack.
        if (parentMerged && parentRun?.BaseBranchOr(project.BaseBranch) is { } mergedInto
            && mergedInto != project.BaseBranch)
        {
            return StackedParentObservation.ParentMergedElsewhere(
                parentBranch,
                $"the parent task's pull request merged into {mergedInto} rather than the project's own "
                + $"{project.BaseBranch} — it was itself stacked, so this pull request's base cannot be moved "
                + "onto the project's base mechanically without dropping work that branch does not have");
        }

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
            ParentHeadRead branchRead = await ReadRemoteBranchHeadAsync(
                git, repositoryPath, parentBranch, cancellationToken);
            ParentHeadRead? pullRequestRead = null;
            if (branchRead.Commit is null && parentRun?.PullRequestNumber is > 0)
            {
                fetchedRef = ParentHeadRef(parentRun.PullRequestNumber.Value);
                pullRequestRead = await ReadPullRequestHeadAsync(
                    git, repositoryPath, parentRun.PullRequestNumber.Value, fetchedRef, cancellationToken);
                if (pullRequestRead.Commit is null)
                {
                    fetchedRef = null;
                }
            }

            if ((pullRequestRead?.Commit ?? branchRead.Commit) is not { } parentHead)
            {
                // A lookup that FAILED and a ref that does not EXIST are two different facts, and
                // this is where they part company (independent pre-PR review, cycle 1, conformance
                // lens: every failed fetch used to arrive here as ParentUnresolvable, which parks a
                // checkpoint on the assertion that no parent head exists — an unobserved claim on
                // what may have been a network blip, and AGENTS.md's never-guess rule forbids
                // exactly that). A failure is reported as what it was: nothing observed, ask again.
                if (FailedRead(branchRead, pullRequestRead) is { } failed)
                {
                    return StackedParentObservation.Unobservable(
                        $"{failed.Detail}, so nothing was observed about {parentBranch} this look — not whether it "
                        + "exists, and not whether it moved");
                }

                // ParentUnresolvable, then: every lookup answered, and what they answered is that
                // there is no such ref. Two shapes produce it — a parent branch never pushed at all
                // (a child claimed ahead of its parent with --acknowledge-unmet-dependencies, an
                // explicitly supported override), or a parent whose records carry no pull request
                // whose head could stand in for a deleted branch. Stable, in that no later sweep
                // finds a head this one missed; permanent only in the second shape, since the first
                // resolves the moment the parent pushes — which is why the checkpoint's own park
                // names waiting for that delivery as a path rather than only unstacking
                // (independent pre-PR review, cycle 1, adversarial lens). Nothing is claimed about
                // whether the parent MOVED either way — that is still unobserved, and neither
                // reader replays on it.
                string fallbackAccount = pullRequestRead is null
                    ? "and the parent's records carry no pull request whose head could be fetched instead"
                    : "and the parent's own pull-request head — the ref GitHub keeps after a branch is deleted — "
                      + "does not exist either";
                return StackedParentObservation.ParentUnresolvable(
                    parentBranch,
                    $"origin has no branch {parentBranch} {fallbackAccount}, so there is no parent head for this "
                    + "branch to be brought onto");
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
                // retarget is about to aim the pull request. Unobservable whichever way that read
                // came back short — a failed fetch or a base branch origin does not have — because
                // neither is a fact about the PARENT, which is what this verdict speaks about.
                ParentHeadRead baseTipRead = await ReadRemoteBranchHeadAsync(
                    git, repositoryPath, project.BaseBranch, cancellationToken);
                if (baseTipRead.Commit is not { } baseTip)
                {
                    return StackedParentObservation.Unobservable(
                        $"the parent's pull request merged, but origin/{project.BaseBranch}'s own tip could not be "
                        + $"resolved ({baseTipRead.Detail}), so there is no observed commit for the replay to land on");
                }

                return new StackedParentObservation(
                    StackedParentVerdict.ParentMerged, parentBranch, boundary, baseTip,
                    $"the parent task's pull request merged, so this pull request's base moves from "
                    + $"{parentBranch} to {project.BaseBranch} and its own commits replay from {Short(boundary)} "
                    + $"onto {Short(baseTip)}");
            }

            // "Moved to a head this branch does not contain" is the whole of what was observed, and
            // the only honest account of it: --is-ancestor's exit 1 is returned identically by a
            // force-pushed review lap and by an ordinary commit appended since this branch was cut
            // (an append-style project's checks-fix lap), and nothing here read a reflog that could
            // tell them apart. This detail becomes the reopen's recorded reason and any later park's
            // obstruction summary, so a force-push asserted here would be an unobserved fact in an
            // audit field (AGENTS.md's never-guess rule; independent pre-PR review, 2026-09-07,
            // adversarial lens). The replay is the same operation either way.
            return new StackedParentObservation(
                StackedParentVerdict.ParentMoved, parentBranch, boundary, parentHead,
                $"the parent branch {parentBranch} has moved to a head {childRun.Branch} does not contain — a "
                + "force-pushed review lap, or commits appended since this branch was cut — so this branch's own "
                + $"commits replay from {Short(boundary)} onto that new head {Short(parentHead)}");
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
    /// Why one look for a head produced no commit — an unpersisted in-process outcome, so an enum
    /// is right here (TASK-MODEL.md §8).
    /// </summary>
    private enum ParentHeadLookup
    {
        /// <summary>A commit was read.</summary>
        Resolved,

        /// <summary>The remote answered, and it has no such ref — git said so in as many words.</summary>
        Missing,

        /// <summary>The look itself failed, so nothing was observed either way.</summary>
        ReadFailed,
    }

    /// <summary>
    /// One look for a head: the commit when there is one, and an account of why not when there
    /// isn't. The <see cref="Lookup"/>/<see cref="Commit"/> pair is what lets
    /// <see cref="ObserveAsync"/> tell <see cref="StackedParentVerdict.ParentUnresolvable"/> — a
    /// ref the remote does not have — from <see cref="StackedParentVerdict.Unobservable"/>, a read
    /// that could not be made; collapsing both to a bare null is what let a network blip park a
    /// checkpoint on the claim that no parent head exists (independent pre-PR review, cycle 1,
    /// conformance lens).
    /// </summary>
    /// <param name="Commit">Non-null exactly when <paramref name="Lookup"/> is <see cref="ParentHeadLookup.Resolved"/>.</param>
    /// <param name="Detail">Why there is no commit, as a clause a caller's own sentence can carry. Empty on a resolved read.</param>
    private sealed record ParentHeadRead(ParentHeadLookup Lookup, string? Commit, string Detail)
    {
        public static ParentHeadRead Resolved(string commit) =>
            new(ParentHeadLookup.Resolved, commit, string.Empty);

        public static ParentHeadRead Missing(string detail) => new(ParentHeadLookup.Missing, null, detail);

        public static ParentHeadRead ReadFailed(string detail) => new(ParentHeadLookup.ReadFailed, null, detail);
    }

    /// <summary>
    /// Whichever look failed to read, or null when both of them answered — the discriminator
    /// between <see cref="StackedParentVerdict.Unobservable"/> and
    /// <see cref="StackedParentVerdict.ParentUnresolvable"/>. The branch's own look is preferred
    /// when both failed, since it is the ref this child is actually built on.
    /// </summary>
    private static ParentHeadRead? FailedRead(ParentHeadRead branchRead, ParentHeadRead? pullRequestRead) =>
        (branchRead, pullRequestRead) switch
        {
            ({ Lookup: ParentHeadLookup.ReadFailed }, _) => branchRead,
            (_, { Lookup: ParentHeadLookup.ReadFailed }) => pullRequestRead,
            _ => null,
        };

    /// <summary>
    /// Whether a failed <c>git fetch</c> said the remote has no such ref, in git's own words.
    /// That message is the one observation that separates "there is nothing there to fetch" from
    /// "the fetch could not be made" — an unreachable host, an expired credential, a killed
    /// transfer — and the two are different facts carrying different verdicts. Anything git says
    /// that this does not recognise counts as the read failing rather than the ref missing, which
    /// is the direction that proceeds instead of parking a run on an unobserved claim.
    /// </summary>
    private static bool NamesAMissingRemoteRef(string? standardError) =>
        standardError.IsNotBlank()
        && standardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="branch"/>'s tip on origin, freshly fetched — and, when there is no tip to
    /// return, whether that is because origin has no such branch or because the fetch could not be
    /// made at all (see <see cref="ParentHeadRead"/>).
    /// </summary>
    private static async Task<ParentHeadRead> ReadRemoteBranchHeadAsync(
        ProcessRunner git, string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        ProcessResult fetch = await git("git", ["fetch", "origin", branch], repositoryPath, cancellationToken);
        if (fetch.ExitCode != 0)
        {
            return NamesAMissingRemoteRef(fetch.StandardError)
                ? ParentHeadRead.Missing($"origin has no branch {branch}")
                : ParentHeadRead.ReadFailed(
                    $"origin/{branch} could not be fetched: {FirstLine(fetch.StandardError)}");
        }

        ProcessResult head = await git(
            "git", ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}^{{commit}}"],
            repositoryPath, cancellationToken);

        // The fetch succeeded, which is origin confirming it has the branch, so a tracking ref
        // that then will not resolve to a commit is this repository failing to answer — never
        // evidence the branch is gone.
        return head.ExitCode == 0 && head.StandardOutput.Trim().IsNotBlank()
            ? ParentHeadRead.Resolved(head.StandardOutput.Trim())
            : ParentHeadRead.ReadFailed(
                $"origin/{branch} was fetched but its tracking ref would not resolve to a commit");
    }

    /// <summary>
    /// The parent pull request's own head commit, fetched into <paramref name="destinationRef"/>
    /// from <c>refs/pull/&lt;n&gt;/head</c> — the ref GitHub keeps after the branch itself is
    /// deleted, which is the whole reason this fallback exists (the same ref
    /// <c>CreatePrReviewCheckoutAsync</c> already relies on). Reports a missing ref apart from a
    /// failed fetch for the reason <see cref="ReadRemoteBranchHeadAsync"/> does.
    /// </summary>
    private static async Task<ParentHeadRead> ReadPullRequestHeadAsync(
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
            return NamesAMissingRemoteRef(fetch.StandardError)
                ? ParentHeadRead.Missing($"origin has no head ref for pull request #{pullRequestNumber}")
                : ParentHeadRead.ReadFailed(
                    $"pull request #{pullRequestNumber}'s own head ref could not be fetched: "
                    + FirstLine(fetch.StandardError));
        }

        ProcessResult head = await git(
            "git", ["rev-parse", "--verify", "--quiet", $"{destinationRef}^{{commit}}"],
            repositoryPath, cancellationToken);
        return head.ExitCode == 0 && head.StandardOutput.Trim().IsNotBlank()
            ? ParentHeadRead.Resolved(head.StandardOutput.Trim())
            : ParentHeadRead.ReadFailed(
                $"pull request #{pullRequestNumber}'s head ref was fetched but would not resolve to a commit");
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

    /// <summary>
    /// Which door out of its own lifecycle the parent took, in a clause a park message can carry.
    /// Deliberately not <c>TaskDependency.DescribeDeath</c>, which is written for a dependent that
    /// has not dispatched yet and whose only lever is "revise this task's dependencies": a child
    /// already holding a branch of its own has other options, and the park that quotes this one
    /// names them itself. Only ever called once <see cref="StackedEdgeRules.IsDead(Guid?, TaskDependency)"/>
    /// has said the parent is dead, so the final arm is that rule's own third arm rather than a
    /// guess at anything unobserved.
    /// </summary>
    private static string DescribeParentDeath(TaskDependency parent) =>
        parent.State == TaskState.Abandoned
            ? "it was abandoned, which is a dead end by design"
            : parent.State == TaskState.Failed
                ? "it ended Failed, which waits on a human decision (retry, resolve, or abandon) before its "
                  + "branch means anything"
                : "it reads Done having never delivered a pull request that can merge — its own was observed "
                  + "closed without merging, or it never opened one";

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    private static string FirstLine(string? text) =>
        text.IsBlank() ? "no output" : text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
}
