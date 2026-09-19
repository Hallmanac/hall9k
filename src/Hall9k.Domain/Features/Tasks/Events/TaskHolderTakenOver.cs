namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// An owner-role member forced this task away from an absent holder (idea 202383dc, item 4:
/// "an owner-role member can take a task from an absent holder"). Unlike
/// <see cref="TaskHolderReleased"/>, which only ever clears a holder that agrees it is done, this
/// is a unilateral override — written on the operator's own judgment, never on detected absence
/// (presence detection is dead, never parked). It carries both halves of the handoff in one
/// event, since the acceptance criterion asks for one task-stream fact naming "previous holder,
/// new holder, reason, and time": <see cref="PreviousHolderNodeId"/> is whatever
/// <see cref="TaskAggregate.HolderNodeId"/> named the instant before this landed (null when the
/// task had none), and every field from <see cref="NewHolderNodeId"/> on describes the taker.
/// <para>
/// Also carries the task past its old claim the same way <see cref="TaskRequeued"/> does — clearing
/// the working claim and landing back on <see cref="TaskState.Queued"/> (or
/// <see cref="TaskState.Blocked"/> when a dependency is still open) — and reassigns
/// <see cref="TaskAggregate.AssignedOwnerId"/> to the taker's own owner, so the overrider's own
/// node's ordinary dispatch sweep (which claims only its own owner's Queued work, Decisions Log
/// #34) can pick this task back up on its next pass through the existing foreign-resume path
/// (idea 202383dc, piece C's residual) — never through a second, bespoke claim mechanism.
/// </para>
/// </summary>
public sealed record TaskHolderTakenOver(
    Guid Id,
    Guid? PreviousHolderNodeId,
    Guid NewHolderNodeId,
    Guid NewHolderOwnerId,
    string? NewHolderOwnerRootFingerprint,
    string Reason,
    Guid TakenByOwnerId,
    DateTimeOffset TakenAt);
