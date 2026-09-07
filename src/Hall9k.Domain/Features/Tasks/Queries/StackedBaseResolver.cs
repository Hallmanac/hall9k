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
public sealed record StackedBase(string BaseBranch, Guid? ParentTaskId, string Reason)
{
    /// <summary>Whether this run is actually stacked on a live parent branch.</summary>
    public bool IsStacked => ParentTaskId is not null;
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
}
