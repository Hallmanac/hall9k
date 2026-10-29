using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Epic;

/// <summary>A task that holds a tracker issue, with the epic it sits in and its project, as far as this node knows them.</summary>
public sealed record SharedIssueHolder(TaskListItem Task, EpicDetails? Epic, ProjectDetails? Project);

/// <summary>
/// The holder that bars a new task from an issue, and whether the cure is to join that holder to the
/// new task's solution epic: it is a task of another member project of the same solution that sits
/// outside the epic.
/// </summary>
public sealed record SharedIssueBar(SharedIssueHolder Holder, bool OutsideSolutionEpic);

/// <summary>
/// The one exception to one live task per tracker issue. A second task may hold an issue only inside
/// one solution epic, and only when every live holder is in that same solution epic in a different
/// member project: the case of one issue needing a backend change and a frontend change, each in its
/// own project. Every other case keeps today's refusal. The same facts decide the other half of the
/// rule at closeout, where an issue several projects' tasks hold is closed only when this node can see
/// all of them.
/// </summary>
public static class SharedIssueRule
{
    /// <summary>
    /// The first of <paramref name="holders"/> (in the order given) that bars a new task in
    /// <paramref name="newProject"/> and <paramref name="newEpic"/> from the issue, or null when every
    /// holder admits it. With no solution epic for the new task, or a project that is not a member of
    /// that epic's solution (one unlinked since the task joined), every holder bars it, which is today's rule.
    /// </summary>
    public static SharedIssueBar? FirstBar(
        Guid newProjectId, ProjectDetails? newProject, EpicDetails? newEpic, IReadOnlyList<SharedIssueHolder> holders)
    {
        foreach (SharedIssueHolder holder in holders)
        {
            if (newEpic is not { IsSolutionEpic: true } || !IsMemberOf(newProject, newEpic.SolutionKey))
            {
                return new SharedIssueBar(holder, OutsideSolutionEpic: false);
            }

            bool differentMemberProject = holder.Task.ProjectId != newProjectId
                && IsMemberOf(holder.Project, newEpic.SolutionKey);
            bool inTheEpic = holder.Epic is { IsSolutionEpic: true } held
                && string.Equals(held.SolutionEpicKey, newEpic.SolutionEpicKey, StringComparison.OrdinalIgnoreCase);

            if (!(differentMemberProject && inTheEpic))
            {
                return new SharedIssueBar(holder, OutsideSolutionEpic: differentMemberProject);
            }
        }

        return null;
    }

    private static bool IsMemberOf(ProjectDetails? project, string? solutionKey) =>
        project?.Solution is { Holds: true } membership
        && solutionKey is not null
        && membership.Key.Equals(solutionKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the issue a closing task holds stays open, or null when this rule has nothing to say.
    /// It speaks only for a task in a solution epic. Such an epic is open to tasks from every member
    /// project, and a task in a project this node does not hold could hold the same issue without this
    /// node being able to see it, so while <paramref name="membersNotHeld"/> (the members of the
    /// solution, as <paramref name="membership"/> records it on this node, that this node does not hold)
    /// is not empty the issue stays open whether or not another holder is visible. When this node holds
    /// every member, it stays open only until every other task of a different project in the same
    /// solution epic that holds it is done or abandoned. A node with no record of the solution cannot
    /// name what it is missing, and says something only when it can already see such another task.
    /// </summary>
    public static string? CloseoutHold(
        TaskListItem closing,
        EpicDetails? closingEpic,
        IReadOnlyList<SharedIssueHolder> others,
        SolutionMembership? membership,
        IReadOnlyList<SolutionMember> membersNotHeld,
        bool everyOtherLinkedTaskClosedOut)
    {
        if (closingEpic is not { IsSolutionEpic: true })
        {
            return null;
        }

        bool anotherProjectShares = others.Any(other => other.Task.ProjectId != closing.ProjectId
            && other.Epic is { IsSolutionEpic: true } epic
            && string.Equals(epic.SolutionEpicKey, closingEpic.SolutionEpicKey, StringComparison.OrdinalIgnoreCase));

        if (membership is null)
        {
            return anotherProjectShares
                ? $"its solution epic '{closingEpic.Title}' is shared with another project's task, and this node holds no record of "
                    + "the solution to check that it holds every member project"
                : null;
        }

        if (membersNotHeld.Count > 0)
        {
            string unseen = string.Join(", ", membersNotHeld.Select(member => $"'{member.Name}'"));
            return anotherProjectShares
                ? $"another project's task in the solution epic '{closingEpic.Title}' holds it too, and this node does not hold "
                    + $"{unseen} of the solution '{membership.Name}', so it cannot see every task that holds it"
                : $"the solution epic '{closingEpic.Title}' is open to tasks from every project of the solution '{membership.Name}', "
                    + $"and this node does not hold {unseen}, so it cannot see whether a task there holds it too";
        }

        return !anotherProjectShares || everyOtherLinkedTaskClosedOut
            ? null
            : $"another project's task in the solution epic '{closingEpic.Title}' holds it too and is not yet done or abandoned; "
                + "the last of them to close out closes it";
    }
}
