namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// This owner's already-claimed root fingerprint turned out to be verifiable after all, from a live
/// chain read rather than from the join that first claimed it (task f53fecfd, criterion 4). Never
/// changes <see cref="OwnerRootClaimed.RootFingerprint"/> itself — only the trust that fingerprint
/// carries — so it is its own event rather than a re-append of <see cref="OwnerRootClaimed"/>, which
/// would otherwise overwrite <see cref="OwnerRootClaimed.ClaimedAt"/> with the moment reconciliation
/// happened to run rather than the moment the claim itself was made.
/// </summary>
/// <param name="RootFingerprint">
/// The exact fingerprint the chain read that produced this event actually found enrolled — never
/// implicitly "whatever the owner currently claims", since a stale in-memory aggregate or a racing
/// concurrent append can each mean the owner's real, current claim has already moved on to a
/// different root by the time this event is applied. <see cref="OwnerAggregate.Apply(OwnerRootVerified)"/>
/// only flips <see cref="OwnerAggregate.RootFingerprintVerified"/> when this still matches
/// <see cref="OwnerAggregate.RootFingerprint"/> at that point in the stream, so a verification
/// computed against a root that is no longer the current claim is silently ignored rather than
/// wrongly marking an unrelated, never-actually-vouched claim as verified (independent pre-PR
/// review, adversarial lens, medium — both <c>h9k project join</c>'s own inline reconciliation and
/// the daemon's <c>MessageSweepEngine</c> tick can otherwise race a concurrent root claim this way).
/// </param>
public sealed record OwnerRootVerified(Guid Id, string RootFingerprint, DateTimeOffset VerifiedAt);
