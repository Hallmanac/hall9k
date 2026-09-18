namespace Hall9k.Domain.Features.Node;

/// <summary>
/// The owner root fingerprint that minted the invite this node joined a project on (idea 202383dc,
/// M2b, task 9408d525) — recorded so a later brand-new bootstrap
/// (<c>Hall9k.Daemon.Messaging.MessageSweepEngine.ResolveVoucherNodeId</c>) can name the inviting
/// owner as its own voucher tier, the ranking <c>Hall9k.Connectors.Replication.EventCatchUpCoordinator</c>
/// already implements but which a hard-coded null voucher left unreachable in production. Node-scoped
/// (the Node stream is install-wide, never per-project) and overwritten on a later join with a
/// different invite, mirroring <see cref="NodeOwnerClaimed"/>'s own "latest claim wins" discipline —
/// never appended when a join establishes its own genesis root instead of redeeming an invite.
/// </summary>
public sealed record NodeInviterRecorded(Guid Id, string InviterOwnerRootFingerprint, DateTimeOffset RecordedAt);
