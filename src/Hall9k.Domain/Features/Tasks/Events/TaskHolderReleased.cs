namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The ledger holder lock (idea 202383dc, A3b) was cleared for this task — true completion,
/// <c>h9k task abandon</c>, or a sweep that found the holding node's own run gone. Never
/// <c>h9k task release</c>: that command remains scoped to this node's own interactive claim, which
/// never writes a ledger holder in the first place (the sentinel <c>Guid.Empty</c> node id
/// <see cref="TaskAggregate.IsInteractiveClaim"/> reads back — deliberately excluded, since the
/// ledger never carries a holder write for one at all). Carries no node id: unlike <see cref="TaskClaimed"/>, which names who is taking
/// responsibility, this only ever means "nobody is" — <see cref="TaskAggregate.Apply(TaskHolderReleased)"/>
/// clears whatever <see cref="TaskAggregate.HolderNodeId"/> already held rather than confirming it
/// against one named here, since every caller only ever appends this after deciding, from its own
/// read, that this task's holder is (or was) itself.
/// <para>
/// <see cref="GrantedToNodeId"/> and <see cref="GrantedToOwnerId"/> are the one exception (idea
/// 202383dc, item 5, "a member can ask a holder for a task"): a cooperative grant — auto or
/// <c>h9k task grant</c> — releases the ledger holder exactly the way any other release does, but
/// names who it was released FOR, so <see cref="TaskAggregate.Apply(TaskHolderReleased)"/> can
/// also reassign <see cref="TaskAggregate.AssignedOwnerId"/> and land the task back on
/// <see cref="TaskState.Queued"/> (or <see cref="TaskState.Blocked"/>) for the requester's own
/// node to claim through the ordinary lock — the identical reassign-and-requeue shape
/// <see cref="TaskHolderTakenOver"/> already performs for a forced takeover. Both null for every
/// ordinary release (true completion, <c>h9k task abandon</c>, the sweep that finds a run gone),
/// which behaves exactly as before this feature existed.
/// </para>
/// </summary>
/// <param name="GrantedToOwnerFingerprint">
/// <see cref="GrantedToOwnerId"/>'s own cross-node root fingerprint (idea 20723ef8) — added
/// beside it, never a retyping, mirroring <see cref="TaskAssigned.AssignedOwnerRootFingerprint"/>'s
/// existing precedent. Carries whatever <c>ClaimRequestWatchLoop.IsRequesterOwnerVerified</c>
/// already verified against the ledger's own trust chain before this grant's own
/// <see cref="TaskTakeRequested"/> was ever appended — the granting node cannot itself verify a
/// foreign owner's Guid (Owner events never replicate), so it is <see cref="GrantedToOwnerId"/>
/// that stays self-declared and unverifiable cross-node, while this field is the one fact every
/// reading node can actually check against its own local record of itself — the daemon's own
/// dispatch claim gate is where that check lives. Null for every ordinary release (no grantee at
/// all) and for any event written before this field existed.
/// </param>
public sealed record TaskHolderReleased(
    Guid Id,
    DateTimeOffset ReleasedAt,
    Guid? GrantedToNodeId = null,
    Guid? GrantedToOwnerId = null,
    string? GrantedToOwnerFingerprint = null);
