namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// This node's own message sweep no longer finds the rotation <see cref="RootRotationObserved"/>
/// raised among <paramref name="RootFingerprint"/>'s own live root keys — a validated
/// <c>owners/&lt;root&gt;/revoked-successors/&lt;node-id&gt;.yaml</c>, signed by a key ranked above
/// it, voided <paramref name="PromotedNodeId"/>'s own rank and everything the chain built on top of
/// it. <paramref name="RevokedByNodeId"/> names the node whose own key is now the current top of the
/// chain (null when that is the root's own K0 rather than a later rotation) — the reader has no
/// standing record of which key actually signed the revoking commit, only what the chain's own top
/// looks like once it is recomputed, so this names who now outranks the voided key rather than who
/// specifically struck it down.
/// </summary>
public sealed record RootRotationRevoked(
    Guid ProjectId, string RootFingerprint, Guid PromotedNodeId, Guid? RevokedByNodeId, DateTimeOffset At);
