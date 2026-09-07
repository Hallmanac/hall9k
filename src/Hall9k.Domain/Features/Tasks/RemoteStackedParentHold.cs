namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The one rule about which observations of a remote stacked parent need a human rather than more
/// patience (task: a stacked child can stand on a pull request another install owns). Three readers
/// derive the hold — <see cref="TaskAggregate"/>, <see cref="Projections.TaskDetails"/> and
/// <see cref="Projections.TaskListItem"/> — and a rule written three times is a rule that comes to
/// disagree, which for a hold means the task surface and the attention pane telling a human
/// opposite things about the same pull request.
/// <para>
/// Derived rather than carried on <see cref="Events.RemoteStackedParentObserved"/> for the same
/// reason: an event carries what was <em>observed</em>, and which observations warrant a human is a
/// standing rule this build holds, not a fact the sweep saw.
/// </para>
/// </summary>
public static class RemoteStackedParentHold
{
    /// <summary>
    /// Why a human is needed, or null when nothing here needs one. Only a pull request that closed
    /// without merging qualifies — slice two's dead-parent rule, one pull request over: its branch
    /// is a base nothing further arrives on, and nothing about that changes on its own. Every other
    /// state is either a release (<see cref="RemoteParentState.Open"/>,
    /// <see cref="RemoteParentState.Merged"/>) or ordinary waiting — a pull request the teammate has
    /// not opened yet (<see cref="RemoteParentState.Absent"/>) resolves itself the moment they do,
    /// and an unobserved one (<see cref="RemoteParentState.Unknown"/>) is a claim nobody has made.
    /// </summary>
    public static string? ReasonFor(RemoteParentState state, int pullRequestNumber) =>
        state == RemoteParentState.ClosedUnmerged
            ? $"Pull request #{pullRequestNumber}, which this task is stacked on, closed without merging — its "
              + "branch is a base nothing further arrives on, so there is nothing left here to stack on. Point "
              + "the edge at a live pull request or drop it: h9k task unassign, then h9k task draft, then "
              + "h9k task revise --stacked-on-pull-request <number> or --clear-stacked-on."
            : null;
}
