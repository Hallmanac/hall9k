using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Epic;

/// <summary>One task under a solution epic's copy in another member project, as a plan's refinement session is told of it.</summary>
/// <param name="Id">The task's id, which a revision may name in <c>blocked-by</c>.</param>
/// <param name="ProjectName">The name of the member project that holds the task.</param>
/// <param name="Objective">The task's objective, as the store holds it.</param>
/// <param name="State">The task's state.</param>
public sealed record SolutionMemberTask(Guid Id, string ProjectName, string Objective, TaskState State);

/// <summary>
/// Chooses which tasks of a solution epic's other member projects a plan's refinement session may be told of
/// (a draft's own project already knows its own). A plan stays project-level and node-scoped, so the solution epic is
/// the one route by which it reads other members' work: only the copies of the draft's own solution epic count, only
/// in member projects that are registered and joined on this node and still in the solution, and a task carries
/// only what is needed to decide a blocking edge, never its context or its acceptance criteria.
/// </summary>
public static class SolutionEpicMemberTasks
{
    /// <summary>
    /// The tasks under the copies of <paramref name="ownCopy"/>'s solution epic in the other member projects.
    /// Null when <paramref name="ownCopy"/> is a project epic, or when its own project is no longer a member of the
    /// solution the copy was made for: there is no list to give, which is not the same as a list with nothing in it.
    /// <paramref name="epics"/> and <paramref name="tasks"/> may hold more than the solution epic's own copies and
    /// tasks; the rest is ignored. Ordered by project name, then task id.
    /// </summary>
    public static IReadOnlyList<SolutionMemberTask>? Choose(
        EpicDetails ownCopy,
        IEnumerable<EpicDetails> epics,
        IEnumerable<TaskListItem> tasks,
        SolutionContext context)
    {
        if (ownCopy is not { SolutionEpicKey: { } epicKey, SolutionKey: { } solutionKey })
        {
            return null;
        }

        Dictionary<Guid, ProjectDetails> projects = context.Projects.ToDictionary(project => project.Id);
        ProjectDetails? ownProject = projects.GetValueOrDefault(ownCopy.ProjectId);
        if (ownProject is null
            || !EpicGroup.IsMemberCopy(ownCopy, ownProject)
            || context.ViewOf(solutionKey) is not { } view
            || !view.Lists(ownProject))
        {
            return null;
        }

        HashSet<Guid> held = [.. context.HeldMembers(view).Select(project => project.Id)];

        // Two nodes can link the same member before replication catches up, so a project may hold several copies of
        // the one solution epic with tasks under each; every qualifying copy counts, not just the earliest.
        Dictionary<Guid, EpicDetails> others = epics
            .Where(copy => copy.ProjectId != ownCopy.ProjectId
                && string.Equals(copy.SolutionEpicKey, epicKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(copy.SolutionKey, solutionKey, StringComparison.OrdinalIgnoreCase)
                && held.Contains(copy.ProjectId)
                && EpicGroup.IsMemberCopy(copy, projects.GetValueOrDefault(copy.ProjectId)))
            .ToDictionary(copy => copy.Id);

        return
        [
            .. tasks
                .Where(task => task.EpicId is { } epicId
                    && others.TryGetValue(epicId, out EpicDetails? copy)
                    && copy.ProjectId == task.ProjectId)
                .Select(task => new SolutionMemberTask(task.Id, projects[task.ProjectId].Name, task.Objective, task.State))
                .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(task => task.Id),
        ];
    }
}
