namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The node that owns a held task took its feedback step: from here on no node starts another for this task, because the
/// aggregate's <see cref="TaskAggregate.FeedbackStepBegun"/> stays true for good. Appended under a version fence by one node
/// only, the node the task is placed on or, with no placement, a node of the owner it is queued for.
/// <para>
/// It carries what the requeue restores after the dequeue clears it: the owner the task was queued for, that owner's
/// fingerprint, and the node it was placed on. The assignee is the queued-for owner, since the go signal sets both.
/// </para>
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="NodeId">The node that runs the step.</param>
/// <param name="StartedAt">When the step began.</param>
/// <param name="QueuedForOwnerId">The owner the task was queued for before the step.</param>
/// <param name="QueuedForOwnerFingerprint">That owner's cross-node root fingerprint, or null when the assignment carried none.</param>
/// <param name="PlacedOnNodeId">The node the task was placed on before the step, or null when it had no placement.</param>
/// <param name="BlockerIds">The task's dependencies, whose notes the check reads.</param>
public sealed record FeedbackStepStarted(
    Guid Id,
    Guid NodeId,
    DateTimeOffset StartedAt,
    Guid? QueuedForOwnerId,
    string? QueuedForOwnerFingerprint,
    Guid? PlacedOnNodeId,
    IReadOnlyList<Guid> BlockerIds);
