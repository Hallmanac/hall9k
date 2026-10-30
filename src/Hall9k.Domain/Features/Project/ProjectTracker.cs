using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Which tracker a project's work lives in, as far as Hall9k enforces one today: a project with a
/// Linear team bound has Linear as its one tracker, and no other tracker's items are linked to its
/// tasks. <see cref="IsLinear"/> is the single predicate that says so, and every refusal in the
/// platform asks it, so the day a project carries one explicit tracker setting (idea c14bfb15) this
/// is the one place that changes. A project with no Linear team behaves exactly as it did before
/// Linear existed.
/// <para>
/// What the rule does not touch: a task's existing links keep their provider and stay readable and
/// closable as history, and a pull request is adopted with <c>--from-pr</c> whatever the tracker.
/// Only a new link has to match the tracker.
/// </para>
/// </summary>
public static class ProjectTracker
{
    /// <summary>The predicate: this project's tracker is Linear.</summary>
    public static bool IsLinear(LinearTeamKey linearTeam) => linearTeam.HasValue;

    /// <summary>
    /// The refusal for a door that would link a Jira card or a GitHub issue to a task of a project
    /// whose tracker is Linear. <paramref name="door"/> names the command, so the message says which
    /// door was refused.
    /// </summary>
    public static void RefuseOtherTracker(LinearTeamKey linearTeam, string projectName, string door)
    {
        if (IsLinear(linearTeam))
        {
            throw new DomainValidationException(
                $"{door} is refused: {projectName} tracks its work in Linear (team {linearTeam.Value}), which is "
                + "its one tracker, so a Jira card or a GitHub issue is not linked to its tasks. Adopt or link "
                + $"a Linear issue instead (h9k task add --from-linear, h9k task link-linear), or to move the "
                + $"project to another tracker clear the Linear team first: h9k project set {projectName} "
                + "--linear none. Adopting a pull request with --from-pr is unaffected.");
        }
    }

    /// <summary>
    /// The one-tracker check for a link of <paramref name="provider"/> on this project, shared by every
    /// door that records one so none restates it: a Jira card or a GitHub issue is refused on a project
    /// bound to Linear, and a Linear issue is refused on one that is not. Any other provider (a pull
    /// request) is no tracker item and is never refused here.
    /// </summary>
    public static void RefuseLinkAgainstTracker(
        WorkItemProvider provider, LinearTeamKey linearTeam, string projectName, string door)
    {
        if (provider == WorkItemProvider.Linear)
        {
            RequireLinear(linearTeam, projectName, door);
        }
        else if (provider == WorkItemProvider.GitHub || provider == WorkItemProvider.Jira)
        {
            RefuseOtherTracker(linearTeam, projectName, door);
        }
    }

    /// <summary>
    /// The refusal for a Linear door on a project whose tracker is not Linear. A Linear issue links a
    /// pull request through the identifier in a branch name, and the branch template only carries one
    /// once a team is bound, so adopting Linear work on an unbound project would never link anything.
    /// </summary>
    public static void RequireLinear(LinearTeamKey linearTeam, string projectName, string door)
    {
        if (!IsLinear(linearTeam))
        {
            throw new DomainValidationException(
                $"{door} needs Linear to be the project's tracker, and {projectName} has no Linear team bound. "
                + $"Bind one first: h9k project set {projectName} --linear <TEAM>");
        }
    }

    /// <summary>
    /// The backlog policy <c>linear</c> files issues into the bound team, so it cannot stand on a
    /// project with none. Judged when this command touches either half of the pair: setting the
    /// policy with no team bound is refused naming the command that binds one, and clearing the team
    /// while the policy stays linear is refused naming the command that clears both. An unrelated
    /// change never trips over a pair a teammate's node left behind (team settings replicate per
    /// field), and a publish survives such a pair through its own pre-commit check.
    /// </summary>
    private static void RefuseLinearPolicyWithoutTeam(
        ProjectAggregate project, Optional<LinearTeamKey> linearTeam, Optional<BacklogPolicy> backlogPolicy)
    {
        BacklogPolicy policyAfter = backlogPolicy.HasValue ? backlogPolicy.Value ?? BacklogPolicy.None : project.BacklogPolicy;
        if (policyAfter != BacklogPolicy.Linear || !(linearTeam.HasValue || backlogPolicy.HasValue))
        {
            return;
        }

        throw new DomainValidationException(
            linearTeam.HasValue
                ? $"--linear none is refused while {project.Name} would still have the backlog policy linear: "
                    + "that policy files each published task's issue in the bound Linear team, and there would "
                    + $"be none. Clear both together: h9k project set {project.Name} --linear none --backlog none"
                : $"--backlog linear is refused: {project.Name} has no Linear team bound, and the policy files "
                    + "each published task's issue in that team. Bind one first, or in the same command: "
                    + $"h9k project set {project.Name} --linear <TEAM>");
    }

    /// <summary>
    /// Judges the settings a <c>project set</c> command leaves behind, not the ones it started from,
    /// so one command can switch trackers (<c>--linear none --jira PROJ</c>, or the reverse).
    /// Binding a team is refused while the project would still have a Jira board, a backlog policy
    /// of github-issues or jira, or a primary tracker, naming the setting to clear; and setting any
    /// of those three on a project that stays on Linear is refused naming Linear as the tracker.
    /// Only what this command touches is judged, so an unrelated setting change never trips over a
    /// combination a teammate's node left behind.
    /// </summary>
    public static void RefuseConflictingSettings(
        ProjectAggregate project,
        Optional<LinearTeamKey> linearTeam,
        Optional<JiraProjectKey> jiraProjectKey,
        Optional<BacklogPolicy> backlogPolicy,
        Optional<WorkItemProvider> primaryTracker)
    {
        LinearTeamKey teamAfter = linearTeam.HasValue ? linearTeam.Value ?? LinearTeamKey.None : project.LinearTeam;
        if (!IsLinear(teamAfter))
        {
            RefuseLinearPolicyWithoutTeam(project, linearTeam, backlogPolicy);
            return;
        }

        JiraProjectKey boardAfter = jiraProjectKey.HasValue ? jiraProjectKey.Value ?? JiraProjectKey.None : project.JiraProjectKey;
        BacklogPolicy policyAfter = backlogPolicy.HasValue ? backlogPolicy.Value ?? BacklogPolicy.None : project.BacklogPolicy;
        WorkItemProvider primaryAfter = primaryTracker.HasValue ? primaryTracker.Value ?? WorkItemProvider.Unknown : project.PrimaryTracker;

        bool boardConflicts = boardAfter.HasValue;
        bool policyConflicts = policyAfter == BacklogPolicy.GitHubIssues || policyAfter == BacklogPolicy.Jira;
        bool primaryConflicts = primaryAfter != WorkItemProvider.Unknown;

        if (linearTeam is { HasValue: true, Value.HasValue: true })
        {
            (bool conflicts, string held, string clear) = (boardConflicts, policyConflicts, primaryConflicts) switch
            {
                (true, _, _) => (true, $"a Jira board ({boardAfter.Value})", "--jira none"),
                (_, true, _) => (true, $"a backlog policy of {policyAfter.Value}", "--backlog none"),
                (_, _, true) => (true, $"a primary tracker ({primaryAfter.Value})", "--primary-tracker none"),
                _ => (false, string.Empty, string.Empty),
            };

            if (conflicts)
            {
                throw new DomainValidationException(
                    $"--linear is refused while {project.Name} would still have {held}: binding a Linear team "
                    + "makes Linear the project's one tracker. Clear it, in the same command if you are "
                    + $"switching trackers: h9k project set {project.Name} {clear} --linear {teamAfter.Value}");
            }

            return;
        }

        (bool setsOther, string option) = (
            jiraProjectKey is { HasValue: true, Value.HasValue: true },
            backlogPolicy.HasValue && policyConflicts,
            primaryTracker.HasValue && primaryConflicts) switch
        {
            (true, _, _) => (true, "--jira"),
            (_, true, _) => (true, "--backlog"),
            (_, _, true) => (true, "--primary-tracker"),
            _ => (false, string.Empty),
        };

        if (setsOther)
        {
            throw new DomainValidationException(
                $"{option} is refused: {project.Name} tracks its work in Linear (team {teamAfter.Value}), which is "
                + "its one tracker, so no Jira board, GitHub issue backlog, or primary tracker is set beside it. "
                + "To move the project to another tracker, clear the Linear team in the same command "
                + $"(h9k project set {project.Name} --linear none, with {option} beside it).");
        }
    }
}
