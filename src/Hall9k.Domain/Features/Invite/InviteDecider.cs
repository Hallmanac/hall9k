using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

public static class InviteDecider
{
    public static InviteMinted Mint(
        Guid inviteId,
        Guid minterNodeId,
        string minterOwnerFingerprint,
        InviteClaimKind claim,
        ProjectMemberRole? role,
        Guid? projectId,
        string? projectRepositoryPath,
        string secret,
        string secretHash,
        DateTimeOffset mintedAt,
        DateTimeOffset expiresAt)
    {
        if (minterOwnerFingerprint.IsBlank())
        {
            throw new DomainValidationException("An invite needs the minting node's own owner root fingerprint.");
        }

        if (claim != InviteClaimKind.NodeOfOwner && claim != InviteClaimKind.MemberOfProject)
        {
            throw new DomainValidationException(
                $"An invite's claim must be {InviteClaimKind.NodeOfOwner} or {InviteClaimKind.MemberOfProject} "
                + "(idea 202383dc, T2).");
        }

        if (claim == InviteClaimKind.MemberOfProject
            && (role is null || projectId is null || projectId == Guid.Empty || projectRepositoryPath.IsBlank()))
        {
            throw new DomainValidationException(
                $"A {InviteClaimKind.MemberOfProject} invite needs the new member's own role and the project "
                + "it is minted for.");
        }

        if (claim == InviteClaimKind.NodeOfOwner && (role is not null || projectId is not null))
        {
            throw new DomainValidationException(
                $"A {InviteClaimKind.NodeOfOwner} invite names no single project or member role — it is written "
                + "into every non-archived project the minting owner is registered to.");
        }

        if (secret.IsBlank() || secretHash.IsBlank())
        {
            throw new DomainValidationException("An invite needs its own secret and the secret's hash.");
        }

        if (expiresAt <= mintedAt)
        {
            throw new DomainValidationException("An invite must expire after it is minted.");
        }

        return new InviteMinted(
            inviteId, minterNodeId, minterOwnerFingerprint, claim, role, projectId, projectRepositoryPath,
            secret, secretHash, mintedAt, expiresAt);
    }

    public static InviteSpent Spend(
        InviteAggregate invite, Guid claimedByNodeId, string claimedByRootFingerprint, DateTimeOffset spentAt)
    {
        if (invite.Spent)
        {
            throw new DomainValidationException($"Invite {invite.Id} is already spent — single use, idea 202383dc T2.");
        }

        if (claimedByRootFingerprint.IsBlank())
        {
            throw new DomainValidationException("Spending an invite needs the claiming node's own root fingerprint.");
        }

        return new InviteSpent(invite.Id, claimedByNodeId, claimedByRootFingerprint, spentAt);
    }

    public static InviteProjectVouched VouchProject(
        InviteAggregate invite, Guid projectId, DateTimeOffset vouchedAt, Guid candidateNodeId,
        string candidateKeyFingerprint, string candidateOwnerFingerprint)
    {
        if (invite.Spent)
        {
            throw new DomainValidationException($"Invite {invite.Id} is already spent — single use, idea 202383dc T2.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("Recording an invite's vouch write needs the project it landed in.");
        }

        if (candidateKeyFingerprint.IsBlank() || candidateOwnerFingerprint.IsBlank())
        {
            throw new DomainValidationException(
                "Recording an invite's vouch write needs the candidate's own key and owner fingerprints, so a "
                + "later sweep tick can tell this candidate apart from a different one matching the same invite.");
        }

        return new InviteProjectVouched(invite.Id, projectId, vouchedAt, candidateNodeId, candidateKeyFingerprint, candidateOwnerFingerprint);
    }
}
