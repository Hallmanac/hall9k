namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Claimed (an untouched interactive claim) -> Published, in one event: the atomic form of
/// <c>h9k task release --unassign</c> (task: h9k task release gains an atomic --unassign
/// option). Everything <see cref="TaskRequeued"/> and <see cref="TaskUnassigned"/> would do in
/// sequence — give the claim back, then take the resulting Queued/Blocked task out of the
/// dispatcher's sight — happens on this one event instead, so the intermediate Queued/Blocked
/// state this task would otherwise pass through is never written to the stream at all. There is
/// no window in which a dispatcher reading the stream could see this task claimable, because
/// that state never existed — the race a two-step release-then-unassign cannot avoid (the
/// dispatcher claiming within seconds at ceiling 4, cd7e0202, 2026-09-04) is closed by
/// construction rather than by timing.
/// </summary>
/// <param name="ClearInteractiveMode">
/// Same meaning as <see cref="TaskRequeued.ClearInteractiveMode"/>: true for a default
/// <c>h9k task release --unassign</c>, false when the operator passed <c>--keep-interactive</c>
/// (task: the release ruling of 2026-09-05 — a default release is an exit door, so headless
/// dispatch must not keep gating phase boundaries for a human who walked away).
/// </param>
public sealed record TaskInteractiveClaimUnassigned(
    Guid Id,
    DateTimeOffset UnassignedAt,
    bool ClearInteractiveMode = false);
