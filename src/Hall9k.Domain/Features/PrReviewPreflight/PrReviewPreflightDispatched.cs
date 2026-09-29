using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// The pull-request review pre-flight (idea 6be68ee2, finding 1, phase one): a short security lap
/// that reads a pull request's own changed-file list and matched diff hunks through <c>gh</c>,
/// with no worktree, before any checkout is cut. Its own stream, keyed by <see cref="Id"/>, a
/// fresh id minted the same way <c>CourierRunDispatched</c>'s own is — there is no
/// <see cref="Tasks.TaskAggregate"/> event this belongs on, since a pull request's head can move
/// between a pre-flight and the task's next dispatch, and a verdict is a fact about ONE judged
/// oid, never the task's own current state.
/// </summary>
/// <param name="DispatchingRunId">
/// The run id of the claim this pre-flight was dispatched to gate — <c>RunLauncher.LaunchAsync</c>'s
/// own <c>runId</c> parameter, the same id <see cref="Tasks.Events.TaskClaimed.RunId"/> carried at
/// that claim (independent pre-PR review, cycle 5, adversarial lens, RunSupervisor.cs:275): a
/// pre-flight that outlives the claim it was dispatched for — reclaimed after a long daemon restart,
/// or released by a human — must never requeue or park a task whose live claim now belongs to a
/// different, later dispatch. <see cref="RunSupervisor.CompletePreflightAsync"/> and
/// <see cref="RunSupervisor.AbandonPreflightAsync"/> both compare this against the task's own
/// current run id before acting, exactly the way both already refuse to act once the task is no
/// longer <see cref="Tasks.TaskState.Claimed"/> at all. Not necessarily this event's own original
/// value for the rest of the row's life: <see cref="PrReviewPreflightReclaimed"/> retargets it when
/// a still-in-flight pre-flight is found dispatched for an earlier, now-superseded claim
/// (independent pre-PR review, cycle 7, conformance lens) — the field always names whichever claim
/// is live now, not necessarily the one that started this pre-flight.
/// </param>
/// <param name="IsMentionFollowUp">
/// True when this pre-flight gates a mention follow-up's own checkout
/// (<c>RunLauncher.LaunchPrReviewMentionFollowUpAsync</c>) rather than an ordinary review
/// dispatch — read back by <c>RunSupervisor.CompletePreflightAsync</c> so a safe verdict releases
/// the task through <see cref="Tasks.RequeueReason.PrReviewPreflightSafeMentionFollowUp"/> instead
/// of the ordinary <see cref="Tasks.RequeueReason.PrReviewPreflightSafe"/>, and the next claim
/// answers the comment instead of running a fresh full review.
/// </param>
public sealed record PrReviewPreflightDispatched(
    Guid Id,
    Guid TaskId,
    Guid DispatchingRunId,
    Guid NodeId,
    AgentModel Model,
    string HeadRefOid,
    IReadOnlyList<string> Surfaces,
    DateTimeOffset DispatchedAt,
    bool IsMentionFollowUp = false);
