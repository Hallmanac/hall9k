using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Published -> Queued (or Blocked): the dispatch trigger, and always an explicit human act
/// (Decisions Log #34). Assignment is the only way a task becomes claimable, and the claim
/// guard reads <see cref="AssignedOwnerId"/> — a node claims only its own owner's work.
/// <see cref="UnmetDependencies"/> is the dependency set as observed at assignment time:
/// empty means Queued, anything else means Blocked until each one reaches true closeout.
/// </summary>
/// <param name="AssignedOwnerRootFingerprint">
/// The assigned owner's cross-node root fingerprint beside <see cref="AssignedOwnerId"/>'s local
/// Guid (idea 202383dc, event stamping, criterion 4) — an added field, never a retyping of
/// <see cref="AssignedOwnerId"/>. Mirrored onto <see cref="TaskAggregate.AssignedOwnerFingerprint"/>
/// (idea f72138e1) so a task assigned on one node of an owner is claimable by every other node of
/// that same owner: the assigning node's own <c>OwnerDetails</c> is the only place that fingerprint
/// is known, since Owner events are OwnerScoped and never replicate. Absent on every event this
/// platform wrote before that task landed; a reader on the assigning node itself can still resolve
/// it after the fact through the identity core's own Guid-to-fingerprint mapping
/// (<see cref="Owner.OwnerRootFingerprintResolver"/>) rather than treating a null here as "no
/// fingerprint exists" — but a peer node has no such fallback, which is exactly why the claim gate
/// only ever falls back to comparing local Guids for an event this old.
/// </param>
/// <param name="PlacedOnNodeId">
/// Advisory dispatch placement narrower than <see cref="AssignedOwnerRootFingerprint"/> (idea
/// 202383dc: an owner can place a task on one of their own nodes rather than leaving it to
/// whichever of their nodes' dispatchers gets there first). <see cref="Optional{T}"/> of a
/// nullable node id, the same present-with-null-clears idiom <see cref="TaskRevised.EpicId"/>
/// already uses: absent (<c>h9k task assign</c> with no <c>--node</c>) leaves whatever placement
/// the task already carried alone, present with null (<c>--node</c> with nothing named) clears it,
/// present with a node id pins it. Never a security decision on its own — the assignment's own
/// owner match already decided whose work this is; this only narrows WHICH of that owner's nodes
/// claims it, and a takeover or a cooperative grant moving the task to a different node rewrites
/// this the same way it rewrites <see cref="AssignedOwnerId"/>, so the old node stands down without
/// a second command.
/// </param>
public sealed record TaskAssigned(
    Guid Id,
    Guid AssignedOwnerId,
    IReadOnlyList<Guid> UnmetDependencies,
    DateTimeOffset AssignedAt,
    Guid AssignedByOwnerId,
    string? AssignedOwnerRootFingerprint = null,
    Optional<Guid?> PlacedOnNodeId = default);
