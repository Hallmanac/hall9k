using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Everything a reader watching a stacked child needs to know about the parent it declared, in
/// whichever of the two forms it declared one: a task in this install's records, or a pull request
/// another install owns (task: a stacked child can stand on a pull request another install owns).
/// <para>
/// It exists because the two readers of that question hold different things — closeout holds a
/// <see cref="TaskAggregate"/>, the review loop's own checkpoints hold a <see cref="TaskDetails"/> —
/// and threading four fields through both call sites is exactly how the two come to read the same
/// child differently. One record, two factories, one shape at the seam.
/// </para>
/// <para>
/// The remote fields are the <em>recorded</em> observation, never a fresh read: the closeout
/// watcher's own remote-parent sweep is the single reader of that pull request, so every consumer
/// downstream of it reads one fact rather than each spending its own provider call and reaching a
/// different answer within the same sweep.
/// </para>
/// </summary>
/// <param name="TaskId">The local blocker this child is stacked on, or null.</param>
/// <param name="PullRequestNumber">The pull request on GitHub this child is stacked on, or null.</param>
/// <param name="RemoteState">The last state that pull request was observed in; Unknown for a local parent.</param>
/// <param name="RemoteHeadBranch">The branch that pull request opens FROM, as last observed.</param>
/// <param name="RemoteBaseBranch">The branch that pull request opens INTO, as last observed.</param>
public sealed record StackedParentDeclaration(
    Guid? TaskId,
    int? PullRequestNumber,
    RemoteParentState RemoteState,
    string RemoteHeadBranch,
    string RemoteBaseBranch)
{
    /// <summary>No stacked edge at all — the shape every unstacked task reads as.</summary>
    public static readonly StackedParentDeclaration None =
        new(null, null, RemoteParentState.Unknown, string.Empty, string.Empty);

    /// <summary>Whether the declared parent is a pull request on GitHub rather than a local task.</summary>
    public bool IsRemote => PullRequestNumber is > 0;

    public static StackedParentDeclaration From(TaskAggregate task) => new(
        task.StackedOnTaskId,
        task.StackedOnPullRequestNumber,
        task.RemoteStackedParentState,
        task.RemoteStackedParentHeadBranch,
        task.RemoteStackedParentBaseBranch);

    public static StackedParentDeclaration From(TaskDetails task) => new(
        task.StackedOnTaskId,
        task.StackedOnPullRequestNumber,
        task.RemoteStackedParentState,
        task.RemoteStackedParentHeadBranch,
        task.RemoteStackedParentBaseBranch);
}
