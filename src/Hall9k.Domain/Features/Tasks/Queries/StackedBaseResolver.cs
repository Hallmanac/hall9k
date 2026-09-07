using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Which branch a run's work sits on top of, and why — the one answer every path that cuts a fresh
/// branch resolves at dispatch and freezes on <c>RunDispatched.BaseBranch</c> (task: a stacked
/// pull-request edge exists as an explicit opt-in dependency). Lives in the domain rather than
/// beside the daemon's dispatcher because there are three such paths, not one: the daemon's own
/// <c>RunLauncher</c>, and the CLI's two interactive claims (<c>h9k task work</c> and
/// <c>h9k task start</c>), which must not disagree about which branch a stacked task builds on.
/// </summary>
/// <param name="BaseBranch">
/// The branch to cut from, diff against, and target the pull request at. The project's own base
/// branch for every ordinary run.
/// </param>
/// <param name="ParentTaskId">
/// The stacked parent this base came from, or null when the base is simply the project's own —
/// which includes a stacked child whose parent has already merged, since there is nothing left to
/// stack on there.
/// </param>
/// <param name="Reason">
/// Why this base and not another, in a sentence for the dispatch log. Always populated, including
/// for the ordinary case, so a stacked task that ended up on the project's base has a recorded
/// account of why rather than looking like an unstacked one.
/// </param>
/// <param name="ParentPullRequestNumber">
/// The stacked parent pull request on GitHub this base came from, or null when the parent is local
/// or there is none (task: a stacked child can stand on a pull request another install owns).
/// Exclusive with <paramref name="ParentTaskId"/>, because a child stands on one parent.
/// </param>
public sealed record StackedBase(
    string BaseBranch, Guid? ParentTaskId, string Reason, int? ParentPullRequestNumber = null)
{
    /// <summary>Whether this run is actually stacked on a live parent branch, in either form.</summary>
    public bool IsStacked => ParentTaskId is not null || ParentPullRequestNumber is not null;
}

/// <summary>
/// Resolves <see cref="StackedBase"/> from the task's declared edge and the parent's own observed
/// state. Deliberately forgiving in one direction only: every shape it cannot honour falls back to
/// the project's base branch with the reason recorded, because a stacked child that lands on main
/// is an ordinary pull request a human can still merge, while one refused outright is work that
/// never runs. Nothing here ever infers a stacked edge — it only reads the one a human declared.
/// </summary>
public static class StackedBaseResolver
{
    /// <summary>
    /// The base <paramref name="task"/>'s next run should sit on. Reads the parent's task
    /// projection and, through it, the parent's current run for the branch it actually pushed —
    /// the branch name lives on the run, never on the task, exactly as the follow-up resume path
    /// already reads it.
    /// </summary>
    public static async Task<StackedBase> ResolveAsync(
        IQuerySession query, TaskDetails task, ProjectDetails project, CancellationToken cancellationToken)
    {
        // The remote form is answered first and entirely from the record, without a provider call
        // of its own (task: a stacked child can stand on a pull request another install owns). The
        // closeout watcher's sweep is the one reader of that pull request, and it is also what
        // released this task from Blocked, so by the time a dispatch reaches here the observation
        // it needs is on the stream by construction. Resolving it live instead would put a gh call
        // on the dispatch path and let a transient failure cut a branch from the wrong base.
        if (task.StackedOnPullRequestNumber is { } parentNumber)
        {
            return ResolveRemote(task, project, parentNumber);
        }

        if (task.StackedOnTaskId is not { } parentId)
        {
            return new StackedBase(project.BaseBranch, null, $"not a stacked task — based on {project.BaseBranch}");
        }

        TaskListItem? parent = await query.LoadAsync<TaskListItem>(parentId, cancellationToken);
        if (parent is null)
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on {parentId}, which the platform no longer knows — based on {project.BaseBranch} instead");
        }

        // A parent in another project has a branch in another repository, so there is nothing here
        // to cut from: falling back is the same forgiving direction every other unhonourable shape
        // takes, and it keeps the raw could-not-resolve-start-point git failure this used to
        // produce off the child (independent pre-PR review, cycle 1, adversarial lens).
        // TaskDecider.Publish refuses this edge outright now, which is where a human is taught;
        // this arm is what an edge published before that gate existed lands on.
        if (parent.ProjectId != task.ProjectId)
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on {DomainId.Short(parentId)}, which belongs to another project — its branch is not in "
                + $"this project's repository, so this is based on {project.BaseBranch}");
        }

        RunDetails? parentRun = parent.CurrentRunId is { } parentRunId
            ? await query.LoadAsync<RunDetails>(parentRunId, cancellationToken)
            : null;

        // The parent's merge is the end of the stack edge's usefulness: its branch is deleted at
        // closeout, so cutting from it would fail outright and targeting a pull request at it would
        // be refused by GitHub. Once the parent has closed out, this child is an ordinary task
        // based on the project's own branch — which is also exactly where a retarget would have
        // put it, so a child that dispatches for the first time AFTER its parent merged needs no
        // retarget and no replay.
        if (parent.State == TaskState.Done && parentRun?.State == RunState.Completed)
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on {DomainId.Short(parentId)}, which has closed out — its branch is gone, so this is "
                + $"based on {project.BaseBranch}");
        }

        if (parentRun is null || parentRun.Branch.IsBlank())
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on {DomainId.Short(parentId)}, which has no run carrying a branch to build on — based "
                + $"on {project.BaseBranch} instead");
        }

        return new StackedBase(
            parentRun.Branch, parentId,
            $"stacked on {DomainId.Short(parentId)} — based on its branch {parentRun.Branch}");
    }

    /// <summary>
    /// The base for a child stacked on a pull request another install owns, read entirely off what
    /// the closeout watcher's sweep last observed about it. Forgiving in the same one direction
    /// <see cref="ResolveAsync"/> is: every state but a live open pull request with a head branch
    /// falls back to the project's base with the reason recorded, because a stacked child that
    /// lands on main is an ordinary pull request a human can still merge.
    /// <para>
    /// Public, and synchronous, because it takes no session at all — that is the whole shape of the
    /// remote arm, and hiding it behind <see cref="ResolveAsync"/> would suggest a query it never
    /// makes. A caller that already knows its parent is remote can ask directly.
    /// </para>
    /// </summary>
    public static StackedBase ResolveRemote(TaskDetails task, ProjectDetails project, int parentNumber)
    {
        // Merged is the remote twin of a local parent that has closed out: the head branch is going
        // away, so cutting from it would fail and targeting a pull request at it would be refused.
        // A child that dispatches for the first time AFTER its parent merged is an ordinary task on
        // the project's base — which is also exactly where a retarget would have put it, so it
        // needs no retarget and no replay.
        if (task.RemoteStackedParentState == RemoteParentState.Merged)
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on pull request #{parentNumber}, which has merged — its head branch is gone, so this "
                + $"is based on {project.BaseBranch}",
                ParentPullRequestNumber: null);
        }

        if (task.RemoteStackedParentState != RemoteParentState.Open
            || task.RemoteStackedParentHeadBranch.IsBlank())
        {
            return new StackedBase(
                project.BaseBranch, null,
                $"stacked on pull request #{parentNumber}, last observed "
                + $"{task.RemoteStackedParentState.Describe()} with no head branch to build on — based on "
                + $"{project.BaseBranch} instead",
                ParentPullRequestNumber: null);
        }

        return new StackedBase(
            task.RemoteStackedParentHeadBranch, null,
            $"stacked on pull request #{parentNumber} — based on its head branch "
            + task.RemoteStackedParentHeadBranch,
            ParentPullRequestNumber: parentNumber);
    }

    /// <summary>
    /// What a run that resumes this task's existing branch inherits from the run before it, rather
    /// than re-resolving: where that branch sits.
    /// </summary>
    /// <param name="BaseBranch">
    /// The base the previous run recorded, resolved through <c>RunDetails.BaseBranchOr</c> so it is
    /// never the blank that means the project's own.
    /// </param>
    /// <param name="ForkPointCommit">
    /// That run's own <c>RunDispatched.BaseCommit</c>, or blank when it recorded none.
    /// </param>
    public sealed record ResumedBase(string BaseBranch, string ForkPointCommit);

    /// <summary>
    /// Where <paramref name="task"/>'s existing branch already sits, for a run that resumes it
    /// rather than cutting a fresh one — null when no earlier run is on record to read it from.
    /// <para>
    /// Both facts carry forward rather than being re-observed, because resuming a branch moves
    /// neither of them. The fork point cannot be re-observed at all: a resumed checkout performs no
    /// fresh cut, and git cannot recover a fork point a force-push has rewritten
    /// (<c>RunDispatched.BaseCommit</c>'s own doc). The base branch could be re-resolved through
    /// <see cref="ResolveAsync"/>, and must not be (adversarial review, cycle 4): that reads the
    /// PARENT's current state, and a parent that closed out between the reopen and this launch
    /// answers "the project's base" for a branch still physically carrying the parent's commits
    /// with its pull request still aimed at the parent's branch — which would disarm
    /// <c>StackedParentWatch.IsStackedChild</c>, the merge-bar guard and every stacked prompt
    /// variant for the rest of the branch's life, and leave the replay that is actually owed unable
    /// to dispatch. The recorded base is a fact about the branch; the resolver answers a question
    /// about the parent, which is the right question only when a branch is being cut.
    /// </para>
    /// <para>
    /// Lives here beside <see cref="ResolveAsync"/> and for the same reason: all three dispatch
    /// paths can resume a branch — the daemon's <c>RunLauncher</c> on a follow-up or on a retry that
    /// resumed <c>TaskDetails.RetryBranch</c>, and the CLI's two interactive claims on that same
    /// retry path — and a door that read a resumed branch differently would record a base or a fork
    /// point that contradicts what the branch actually holds (conformance review, cycle 4).
    /// </para>
    /// <para>
    /// The previous run is the newest entry in <see cref="TaskDetails.RunIds"/> that is not
    /// <paramref name="runId"/> — deliberately not <c>CurrentRunId</c>, which by the time a
    /// dispatch reaches here already names THIS run: the claim that produced it committed first,
    /// and this run's own stream does not exist yet, so reading it would resolve nothing and
    /// silently blank both facts on every resume.
    /// </para>
    /// </summary>
    public static async Task<ResumedBase?> ResumedBaseAsync(
        IQuerySession query, TaskDetails task, ProjectDetails project, Guid runId,
        CancellationToken cancellationToken)
    {
        Guid previousRunId = task.RunIds.LastOrDefault(id => id != runId);
        if (previousRunId == Guid.Empty)
        {
            return null;
        }

        RunDetails? previous = await query.LoadAsync<RunDetails>(previousRunId, cancellationToken);
        return previous is null
            ? null
            : new ResumedBase(previous.BaseBranchOr(project.BaseBranch), previous.BaseCommit);
    }
}
