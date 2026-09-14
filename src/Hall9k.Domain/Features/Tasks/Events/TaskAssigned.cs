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
/// <see cref="AssignedOwnerId"/>. Absent on every event this platform wrote before that task
/// landed; a reader resolves it from there through the identity core's own Guid-to-fingerprint
/// mapping (<see cref="Owner.OwnerRootFingerprintResolver"/>) rather than treating a null here as
/// "no fingerprint exists".
/// </param>
public sealed record TaskAssigned(
    Guid Id,
    Guid AssignedOwnerId,
    IReadOnlyList<Guid> UnmetDependencies,
    DateTimeOffset AssignedAt,
    Guid AssignedByOwnerId,
    string? AssignedOwnerRootFingerprint = null);
