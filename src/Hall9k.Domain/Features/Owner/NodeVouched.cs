namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// This owner vouched a node into their own fleet (idea 202383dc, T1): the local record of an
/// <c>h9k node vouch</c> run from one of this owner's nodes. The ledger file
/// (<c>owners/&lt;root&gt;/nodes/&lt;node-id&gt;.yaml</c>) is the fact every other node actually
/// trusts; this event is this node's own audit trail of having issued it; other nodes learn the
/// vouch from the file, never from this event (owner-scoped, never replicated).
/// </summary>
public sealed record NodeVouched(Guid OwnerId, Guid NodeId, string NodeFingerprint, DateTimeOffset IssuedAt);
