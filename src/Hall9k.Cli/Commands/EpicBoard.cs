using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>One member project's copy of an epic, with the tasks under it.</summary>
internal sealed record EpicBoardProject(
    string ProjectName, EpicDetails Copy, IReadOnlyList<TaskStatusRow> Rows, TaskRollup Rollup);

/// <summary>
/// An epic as one thing, however many copies stand behind it: the copies this node holds, the tasks
/// under each grouped by their project, and the rollup over all of them. A project epic is the group
/// of one. For a solution epic a node shows only the tasks of the projects it has joined, and the
/// solution is named from the record this node holds of it.
/// </summary>
internal sealed record EpicBoard(
    EpicGroup Group,
    string? SolutionName,
    IReadOnlyList<EpicBoardProject> Projects,
    TaskRollup Rollup)
{
    public static EpicBoard Compose(EpicGroup group, SolutionContext context, IReadOnlyList<TaskStatusRow> rows)
    {
        List<EpicBoardProject> projects = [];
        foreach (EpicDetails copy in group.Copies.OrderBy(copy => copy.AddedAt).ThenBy(copy => copy.Id))
        {
            ProjectDetails? project = context.Projects.FirstOrDefault(candidate => candidate.Id == copy.ProjectId);
            if (group.IsSolutionEpic && (project is null || !context.IsJoined(project)))
            {
                continue;
            }

            TaskStatusRow[] copyRows = [.. rows.Where(row => row.EpicId == copy.Id).OrderByDescending(row => row.AddedAt)];
            projects.Add(new EpicBoardProject(
                project?.Name ?? $"id {copy.ProjectId}", copy, copyRows, TaskRollup.From(copyRows)));
        }

        projects = [.. projects.OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)];
        string? solutionName = group.SolutionKey is { } key ? context.ViewOf(key)?.Membership.Name : null;
        return new EpicBoard(group, solutionName, projects, TaskRollup.From(projects.SelectMany(project => project.Rows)));
    }
}
