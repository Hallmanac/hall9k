using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

public static class OwnerActHoldDecider
{
    public static OwnerActHeld Hold(
        Guid id, Guid projectId, string projectRepositoryPath, Guid inviteId, Guid requesterNodeId,
        string candidateOwnerFingerprint, ProjectMemberRole role, DateTimeOffset issuedAt, DateTimeOffset heldAt)
    {
        if (projectId == Guid.Empty || projectRepositoryPath.IsBlank())
        {
            throw new DomainValidationException("An owner-act hold needs the project it was requested against.");
        }

        if (candidateOwnerFingerprint.IsBlank())
        {
            throw new DomainValidationException("An owner-act hold needs the candidate member's own root fingerprint.");
        }

        if (role != ProjectMemberRole.Owner)
        {
            throw new DomainValidationException("Only an owner-role write is ever held for approval.");
        }

        return new OwnerActHeld(
            id, projectId, projectRepositoryPath, inviteId, requesterNodeId, candidateOwnerFingerprint, role,
            issuedAt, heldAt);
    }

    public static OwnerActApproved Approve(OwnerActHoldAggregate hold, string? commitId, DateTimeOffset approvedAt)
    {
        if (hold.Approved)
        {
            throw new DomainValidationException($"Owner-act hold {hold.Id} is already approved.");
        }

        if (hold.Expired)
        {
            throw new DomainValidationException(
                $"Owner-act hold {hold.Id} already expired - the requester has moved on; re-mint the invite.");
        }

        return new OwnerActApproved(hold.Id, commitId, approvedAt);
    }

    public static OwnerActHoldExpired Expire(OwnerActHoldAggregate hold, DateTimeOffset expiredAt)
    {
        if (hold.Approved)
        {
            throw new DomainValidationException($"Owner-act hold {hold.Id} is already approved - nothing to expire.");
        }

        if (hold.Expired)
        {
            throw new DomainValidationException($"Owner-act hold {hold.Id} already expired.");
        }

        return new OwnerActHoldExpired(hold.Id, expiredAt);
    }
}
