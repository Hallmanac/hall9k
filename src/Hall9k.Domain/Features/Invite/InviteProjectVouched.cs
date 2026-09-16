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
/// Carries the candidate's own identity (node id, key fingerprint, and owner fingerprint) alongside
/// <c>projectId</c>, not only the project: "already mine" must mean this exact candidate already
/// landed here, never merely that some candidate once did, or a later sweep tick that matches a
/// different candidate to the same still-outstanding invite could skip the collision guard entirely
/// for a slot that candidate never actually earned (independent pre-PR review, cycle 1, adversarial
/// lens, medium — the retry guard, keyed by project alone, let a second candidate ride an earlier
/// candidate's own recorded "already vouched" project straight past <see cref="InviteAggregate"/>'s own
/// slot checks).
/// </summary>
public sealed record InviteProjectVouched(
    Guid InviteId, Guid ProjectId, DateTimeOffset VouchedAt, Guid CandidateNodeId, string CandidateKeyFingerprint,
    string CandidateOwnerFingerprint);
