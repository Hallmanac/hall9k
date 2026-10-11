namespace Hall9k.Domain.Features.Tasks.Events;

/// <param name="ClearInteractiveMode">
/// True when this requeue is itself the human's explicit act of returning the task to the
/// machine — <c>h9k task release</c>'s own default (task: interactive mode becomes a recorded
/// property of the task, design ruling R6, amended 2026-09-05: a default release is an exit door
/// exactly like <c>h9k task handback</c>, since headless dispatch must not gate phase boundaries
/// for a human who walked away). False for every other <see cref="Handlers.TaskDecider.Requeue"/>
/// caller — a node's own lease expiring (<c>DispatchEngine</c>) is not a human decision about
/// interactive mode and must never turn the flag off — and for a release given
/// <c>--keep-interactive</c>, the operator's own explicit ask for a headless run that still parks
/// at each boundary.
/// </param>
/// <param name="LaunchNetworkRetryAttempt">
/// Set only for <see cref="RequeueReason.LaunchNetworkRetry"/>: how many consecutive launches of
/// this task have now failed because git could not reach origin, this one included. Optional and
/// additive, so a node on an older build reads the event as an ordinary requeue and claims the task
/// at its next poll, which is no worse than failing it.
/// </param>
/// <param name="LaunchNetworkRetryBound">
/// The attempt count at which the launch stops waiting and fails the task, as it stood when this
/// event was written. Carried on the event because the CLI cannot read the daemon's options and
/// <c>h9k task show</c> prints it.
/// </param>
/// <param name="LaunchNetworkRetryNotBefore">
/// The earliest moment any node may claim the task again. <see cref="Handlers.TaskDecider.Claim"/>
/// and the dispatcher's queue read both honour it through one predicate.
/// </param>
/// <param name="LaunchNetworkRetryError">Git's own error text for the failed fetch, flattened to one line.</param>
/// <param name="LaunchNetworkRetryMentionFollowUp">
/// True when the launch that could not reach origin was a pr-review mention follow-up's own
/// checkout, so the requeue must keep the follow-up owed: the next claim answers the mentioning
/// comment again (<see cref="TaskAggregate.PendingMentionFollowUpAfterPreflight"/>) instead of
/// running an unrequested full review. Only read for <see cref="RequeueReason.LaunchNetworkRetry"/>.
/// </param>
public sealed record TaskRequeued(
    Guid Id,
    RequeueReason Reason,
    DateTimeOffset RequeuedAt,
    bool ClearInteractiveMode = false,
    int? LaunchNetworkRetryAttempt = null,
    int? LaunchNetworkRetryBound = null,
    DateTimeOffset? LaunchNetworkRetryNotBefore = null,
    string? LaunchNetworkRetryError = null,
    bool LaunchNetworkRetryMentionFollowUp = false);
