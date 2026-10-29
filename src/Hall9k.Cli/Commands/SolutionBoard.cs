using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>One member project of a solution on this node, with the tasks the board holds for it.</summary>
internal sealed record SolutionBoardProject(
    SolutionMember Member, ProjectDetails Project, IReadOnlyList<TaskStatusRow> Rows, TaskRollup Rollup);

/// <summary>A listed member that is registered and joined here but now carries <paramref name="MovedTo"/>, another solution.</summary>
internal sealed record SolutionBoardMovedMember(SolutionMember Member, SolutionMembership MovedTo);

/// <summary>
/// A solution's board across its members, grouped by project: the rows
/// <see cref="TaskStatusComposer"/> already composes for each member this node holds, the
/// <see cref="TaskRollup"/> over them, the members this node has not joined, which have no
/// tasks here to show and are named by their recorded name and repository URL instead, and the
/// members that have since joined another solution, which are named with the solution they moved to.
/// </summary>
internal sealed record SolutionBoard(
    SolutionView Solution,
    IReadOnlyList<SolutionBoardProject> Projects,
    IReadOnlyList<SolutionMember> NotJoined,
    IReadOnlyList<SolutionBoardMovedMember> MovedOn,
    TaskRollup Rollup)
{
    /// <summary>
    /// Composes the board in member order from every row on the node (a row belongs to the board
    /// when its project carries a member's key and none other solution), newest task first within a project.
    /// </summary>
    public static SolutionBoard Compose(
        SolutionView solution,
        IReadOnlyList<ProjectDetails> projects,
        IReadOnlyList<TaskStatusRow> rows,
        Func<Guid, string?> rootFingerprintOf)
    {
        IReadOnlyList<ProjectDetails> candidates = [.. projects.Where(solution.NotInAnotherSolution)];
        List<SolutionBoardProject> held = [];
        List<SolutionMember> notJoined = [];
        List<SolutionBoardMovedMember> movedOn = [];
        foreach (SolutionMember member in solution.Membership.Members)
        {
            if (SolutionDecider.HeldProject(member, candidates, rootFingerprintOf) is not { } project)
            {
                if (SolutionDecider.HeldProject(member, projects, rootFingerprintOf) is { Solution: { } movedTo })
                {
                    movedOn.Add(new SolutionBoardMovedMember(member, movedTo));
                }
                else
                {
                    notJoined.Add(member);
                }

                continue;
            }

            TaskStatusRow[] projectRows = [.. rows.Where(row => row.ProjectId == project.Id).OrderByDescending(row => row.AddedAt)];
            held.Add(new SolutionBoardProject(member, project, projectRows, TaskRollup.From(projectRows)));
        }

        return new SolutionBoard(solution, held, notJoined, movedOn, TaskRollup.From(held.SelectMany(project => project.Rows)));
    }
}

/// <summary>Resolves <c>--solution</c> on a list command to the member projects this node knows.</summary>
internal static class SolutionScope
{
    /// <summary>
    /// The ids of the projects on this node that are members of the named solution: every project
    /// holding a listed member's key that carries no other solution, which is the board's own reading
    /// of membership. A project the record does not list, even one carrying it, is not a member. A member this
    /// node has no project for contributes nothing.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> ProjectIdsAsync(
        IQuerySession session, string nameOrKeyPrefix, CancellationToken cancellationToken)
    {
        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        return ProjectIds(SolutionRegistry.Match(context.Solutions, nameOrKeyPrefix), context.Projects);
    }

    public static IReadOnlySet<Guid> ProjectIds(SolutionView solution, IReadOnlyList<ProjectDetails> projects) =>
        new HashSet<Guid>(projects
            .Where(project => solution.NotInAnotherSolution(project)
                && project.ProjectKey is { } key
                && solution.Membership.Members.Any(member => member.ProjectKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Select(project => project.Id));
}
