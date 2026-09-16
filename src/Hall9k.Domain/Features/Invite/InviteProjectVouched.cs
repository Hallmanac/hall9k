namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The minting node's own sweep landed this invite's vouch write into <c>projectId</c>'s own ledger
/// — this node's sole local record of that fact, checked back before ever writing into that same
/// project again (idea 202383dc, T2). Ledger file content cannot stand in for this record: any node
/// with push access to the repository can write an arbitrary <c>invite_id</c>-shaped field into a
/// path it does not otherwise own (independent pre-PR review, cycle 6, adversarial lens, high — a
/// planted match on that field alone let an invite holder overwrite an unrelated project's own
/// member or node file under the minting node's own signature). This node's own event stream is not
/// forgeable by an invite holder, so it is the only place this "already mine" fact can safely live.
/// </summary>
public sealed record InviteProjectVouched(Guid InviteId, Guid ProjectId, DateTimeOffset VouchedAt);
