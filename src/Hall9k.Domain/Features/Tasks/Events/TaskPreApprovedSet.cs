namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human flips this task's standing pre-approval after publish (task: a task can be published
/// pre-approved) — deliberately state-agnostic in the same sense
/// <see cref="TaskSessionCapOverridden"/> is, but with one guard that override does not carry:
/// refused on Abandoned, and on a Done task whose pull request has already merged — closeout
/// observed it — since neither has a future pull request left for the flag to govern. A Done task
/// whose pull request is still open is not refused: closeout has not yet observed a merge, so
/// pre-approval still has something left to govern. <see cref="TaskDecider.SetPreApproved"/> is
/// the only place that guard is enforced.
/// </summary>
public sealed record TaskPreApprovedSet(
    Guid Id,
    bool PreApproved,
    DateTimeOffset SetAt,
    Guid SetByOwnerId);
