using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// Whether a published draft was left without a tracker item on a project that tracks a backlog. Asked by the daemon
/// when it ends a plan and by <c>h9k plan show</c>, so the two say the same thing: the project tracks a backlog, the
/// draft carries no linked item and none is on its way (a publication request outstanding), and it was not
/// published untracked, which is the person's deliberate choice and so no gap.
/// </summary>
public static class TrackerGap
{
    /// <summary>Whether this published task is a gap.</summary>
    public static bool Exists(BacklogPolicy policy, TaskDetails published) =>
        (policy == BacklogPolicy.Jira || policy == BacklogPolicy.GitHubIssues || policy == BacklogPolicy.Linear)
        && published.State != TaskState.Draft
        && string.IsNullOrWhiteSpace(published.ExternalReference)
        && string.IsNullOrWhiteSpace(published.PendingPublicationProvider)
        && !published.UntrackedAttested;
}
