namespace Hall9k.Domain.Features.Node;

/// <summary>
/// The owner fingerprint this node currently claims — either its own key's fingerprint (a
/// genesis <c>h9k project join</c> with no <c>--owner</c>) or a fingerprint named by
/// <c>--owner</c>, unverified until the team half's vouch confirms it (not yet built).
/// Re-appended, not replaced, on every <c>h9k project join</c> that changes the claim — join is
/// re-runnable for exactly this reason (idea 202383dc, A2a, the recovery rule).
/// </summary>
public sealed record NodeOwnerClaimed(Guid Id, string OwnerFingerprint, DateTimeOffset ClaimedAt);
