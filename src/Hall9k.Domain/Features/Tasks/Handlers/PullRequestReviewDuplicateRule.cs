using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// Which of several live auto-created pr-review tasks on one pull request survives, and which of
/// them count as its rivals at all. One request from GitHub earns one live task across every node
/// of an owner's fleet; two nodes that both mint for it before replication lands each hold a task
/// the other cannot yet see, and this rule is what both of them apply once they can.
/// <para>
/// The survivor is the smallest task id. A task id is a UUIDv7, so the smallest id is the oldest
/// mint, but the comparison never reads a wall clock: two nodes whose clocks disagree still name
/// the same survivor, because an id is the one fact both nodes hold identically. The comparison is
/// made here and nowhere else; the convergence pass and the dispatcher's claim-time refusal both
/// ask this type rather than each ordering ids on its own.
/// </para>
/// </summary>
public static class PullRequestReviewDuplicateRule
{
    /// <summary>
    /// Whether this task takes part in duplicate convergence at all: an auto-created pr-review task
    /// that is not yet Done or Abandoned and belongs to this owner. A teammate's task on the same
    /// pull request is a different owner's work and is never a rival, and a task a human adopted by
    /// hand is never abandoned on the platform's own judgment.
    /// </summary>
    public static bool IsRival(TaskListItem task, Guid ownerId, string? ownerRootFingerprint) =>
        task.Type == TaskType.PrReview
        && task.WasAutoPrReviewCreated
        && !task.State.IsTerminal
        && TaskDecider.IsGrantedToThisOwner(
            task.AssignedOwnerId, task.AssignedOwnerFingerprint, ownerId, ownerRootFingerprint);

    /// <summary>The task that survives among these live rivals: the one with the smallest id.</summary>
    public static Guid SurvivorOf(IEnumerable<Guid> liveTaskIds) => liveTaskIds.Min();

    /// <summary>Whether <paramref name="taskId"/> is the younger twin of <paramref name="otherLiveTaskId"/>, that is, whether the other one survives it.</summary>
    public static bool IsYoungerThan(Guid taskId, Guid otherLiveTaskId) => otherLiveTaskId.CompareTo(taskId) < 0;

    /// <summary>
    /// The reason recorded on the abandon. The survivor's id leads it, because the courier feed
    /// clips a reason at 160 characters and the id is the one part a reader cannot do without.
    /// </summary>
    public static string AbandonReason(Guid survivorId) =>
        $"Duplicate of task {survivorId}: two nodes each created a review task for one GitHub review "
        + "request, and the smaller id survives.";
}
