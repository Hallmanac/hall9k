namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// This owner's already-claimed root fingerprint turned out to be verifiable after all, from a live
/// chain read rather than from the join that first claimed it (task f53fecfd, criterion 4). Never
/// changes <see cref="OwnerRootClaimed.RootFingerprint"/> itself — only the trust that fingerprint
/// carries — so it is its own event rather than a re-append of <see cref="OwnerRootClaimed"/>, which
/// would otherwise overwrite <see cref="OwnerRootClaimed.ClaimedAt"/> with the moment reconciliation
/// happened to run rather than the moment the claim itself was made.
/// </summary>
public sealed record OwnerRootVerified(Guid Id, DateTimeOffset VerifiedAt);
