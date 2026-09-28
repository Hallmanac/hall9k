namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// Whether this sweep's own freshly computed labels differ from what this project's stream last
/// recorded (task b7d8222e) — the identical change-only shape
/// <see cref="Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ObserveGitHubCollaborators"/>
/// already applies to its own roster event, so a project with a settled team appends nothing tick
/// after tick.
/// </summary>
public static class ProjectMemberLabelsDecider
{
    public static ProjectMemberLabelsObserved? Observe(
        ProjectMemberLabels? existing, Guid projectId, IReadOnlyList<ProjectMemberLabel> labels, DateTimeOffset at) =>
        existing is not null && LabelsEqual(existing.Labels, labels)
            ? null
            : new ProjectMemberLabelsObserved(projectId, labels, at);

    /// <summary>
    /// Set equality by root fingerprint, comparing each member's own fields — never a plain
    /// <c>SequenceEqual</c> over the records themselves, since <see cref="ProjectMemberLabel.FleetNodeIds"/>
    /// is a list and two lists holding the identical node ids in a different order must never read
    /// as a change (<c>ProjectDecider.ObserveGitHubCollaborators</c>'s own doc names the identical
    /// hazard for its roster).
    /// </summary>
    private static bool LabelsEqual(IReadOnlyList<ProjectMemberLabel> before, IReadOnlyList<ProjectMemberLabel> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        Dictionary<string, ProjectMemberLabel> byRoot = before.ToDictionary(label => label.RootFingerprint);
        foreach (ProjectMemberLabel label in after)
        {
            if (!byRoot.TryGetValue(label.RootFingerprint, out ProjectMemberLabel? previous)
                || previous.DisplayName != label.DisplayName
                || previous.DeclaredLogin != label.DeclaredLogin
                || !new HashSet<Guid>(previous.FleetNodeIds).SetEquals(label.FleetNodeIds))
            {
                return false;
            }
        }

        return true;
    }
}
