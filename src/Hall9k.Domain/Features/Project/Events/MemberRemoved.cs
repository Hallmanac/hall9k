namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A root fingerprint's membership was removed from this project (idea 202383dc, T1) — the local
/// record of an <c>h9k project member remove</c> run, mirroring <see cref="MemberVouched"/>.
/// Removal deletes the ledger file itself (never a tombstone), so this event is only ever this
/// node's own audit trail of having issued that delete.
/// </summary>
public sealed record MemberRemoved(Guid ProjectId, string RootFingerprint, DateTimeOffset RemovedAt);
