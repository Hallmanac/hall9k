namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The current holder refused a member's <see cref="TaskTakeRequested"/> (idea 202383dc, item 5):
/// automatically, because a run was live for this task at the moment the request landed (
/// <see cref="Reason"/> names the run's own start time), or by a human's own
/// <c>h9k task refuse --reason</c> under <c>take-policy ask</c>. Carries no holder change of its
/// own — the ledger and the task's claim are both untouched, exactly as they were before the
/// request arrived.
/// </summary>
public sealed record TaskTakeRefused(
    Guid Id,
    Guid RequesterNodeId,
    Guid RequesterOwnerId,
    string Reason,
    DateTimeOffset RefusedAt);
