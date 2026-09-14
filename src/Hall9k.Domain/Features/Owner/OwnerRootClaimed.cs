namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// This owner's cross-node identity: the ed25519 fingerprint of a root key, either this owner's
/// own (established the first time any of their nodes joins a project with no <c>--owner</c>,
/// <see cref="Verified"/> true) or claimed on their behalf by <c>h9k project join --owner
/// &lt;fingerprint&gt;</c> (<see cref="Verified"/> false until the team half's vouch confirms it —
/// not yet built). Re-appended, never replaced in place, so the Owner stream itself is the
/// history of every claim this owner's nodes have ever made, including the migration of a
/// pre-existing local Guid owner onto its first root fingerprint (idea 202383dc, ruled
/// 2026-09-13).
/// </summary>
public sealed record OwnerRootClaimed(Guid Id, string RootFingerprint, bool Verified, DateTimeOffset ClaimedAt);
