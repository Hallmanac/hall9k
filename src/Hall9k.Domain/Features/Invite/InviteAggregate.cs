using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

public sealed class InviteAggregate
{
    public Guid Id { get; private set; }
    public Guid MinterNodeId { get; private set; }
    public string MinterOwnerFingerprint { get; private set; } = string.Empty;
    public InviteClaimKind Claim { get; private set; } = InviteClaimKind.Unknown;
    public ProjectMemberRole? Role { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string? ProjectRepositoryPath { get; private set; }
    public string Secret { get; private set; } = string.Empty;
    public string SecretHash { get; private set; } = string.Empty;
    public DateTimeOffset MintedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool Spent { get; private set; }
    public DateTimeOffset? SpentAt { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }
    public string? ClaimedByRootFingerprint { get; private set; }

    public void Apply(InviteMinted @event)
    {
        Id = @event.InviteId;
        MinterNodeId = @event.MinterNodeId;
        MinterOwnerFingerprint = @event.MinterOwnerFingerprint;
        Claim = @event.Claim;
        Role = @event.Role;
        ProjectId = @event.ProjectId;
        ProjectRepositoryPath = @event.ProjectRepositoryPath;
        Secret = @event.Secret;
        SecretHash = @event.SecretHash;
        MintedAt = @event.MintedAt;
        ExpiresAt = @event.ExpiresAt;
    }

    public void Apply(InviteSpent @event)
    {
        Spent = true;
        SpentAt = @event.SpentAt;
        ClaimedByNodeId = @event.ClaimedByNodeId;
        ClaimedByRootFingerprint = @event.ClaimedByRootFingerprint;
    }
}
