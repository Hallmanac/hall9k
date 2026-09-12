namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The pre-PR review loop stopped dispatching passes because the pull request it was reviewing
/// had already merged out from under it — a human merging mid-review, the same already-merged
/// guard <c>RunLauncher.TryCloseOutMergedPullRequestAsync</c> applies at dispatch, moved instead
/// to the boundary between review passes (task: a post-PR follow-up's review loop checks the
/// pull request's merge state between passes). Appended immediately before
/// <see cref="PullRequestMerged"/>, <see cref="RunHandoffRecorded"/> and <see cref="RunCompleted"/>
/// land the same closeout every merged run gets, in the same transaction — this event exists only
/// to say WHY that closeout ran without the loop ever reaching <see cref="ReviewPhase.MergeReady"/>
/// on its own: <see cref="ReviewSettled"/>'s own <see cref="ReviewSettlement"/> vocabulary
/// (Clean/Settled/Unknown) has no value honest about a run whose loop never converged at all.
/// </summary>
/// <param name="Cycle">The review cycle in progress when the merge was observed — the cycle whose next pass never dispatched.</param>
/// <param name="ReLandDraftTaskId">
/// The draft task naming the stranded delta (patch and commit list), when this run's worktree
/// carried commits the merged pull request never included. Null when the worktree's tip was
/// already fully reflected in the merge, so there was nothing to strand.
/// </param>
public sealed record ReviewEndedByMerge(
    Guid Id,
    int Cycle,
    Guid? ReLandDraftTaskId,
    DateTimeOffset ObservedAt);
