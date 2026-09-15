namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// This owner revoked a node from their own fleet (idea 202383dc, T1) — the local record of an
/// <c>h9k node revoke</c> run, mirroring <see cref="NodeVouched"/>. The ledger file
/// (<c>owners/&lt;root&gt;/revoked/&lt;node-id&gt;.yaml</c>) is what every other node's own chain
/// read actually honors; a later <see cref="NodeVouched"/> for the identical node id is how a
/// surviving node undoes a bad revocation, since the ledger's own commit order — not this node's
/// local event history — decides which one currently stands.
/// </summary>
public sealed record NodeRevoked(Guid OwnerId, Guid NodeId, DateTimeOffset RevokedAt);
