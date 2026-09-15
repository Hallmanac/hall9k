using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// A single-use invite secret was minted (idea 202383dc, T2): the minting node's own local record,
/// carrying the plaintext <see cref="Secret"/> — kept only here, in this node's own event store,
/// never in the ledger and never printed again after the minting command's own single report.
/// <see cref="Role"/> is set only for <see cref="InviteClaimKind.MemberOfProject"/>;
/// <see cref="ProjectId"/> and <see cref="ProjectRepositoryPath"/> are set only there too — a
/// <see cref="InviteClaimKind.NodeOfOwner"/> invite is written into every non-archived project the
/// minting owner is registered to (the same "owner-wide, every project" shape <c>h9k node
/// vouch</c> already uses), so it names no single project of its own.
/// </summary>
public sealed record InviteMinted(
    Guid InviteId,
    Guid MinterNodeId,
    string MinterOwnerFingerprint,
    InviteClaimKind Claim,
    ProjectMemberRole? Role,
    Guid? ProjectId,
    string? ProjectRepositoryPath,
    string Secret,
    string SecretHash,
    DateTimeOffset MintedAt,
    DateTimeOffset ExpiresAt);
