using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Invite;

/// <summary>Read side of <see cref="InviteAggregate"/> — what the minting node's own sweep queries
/// for its outstanding invites, and what <c>h9k node invite</c>/<c>h9k project invite</c> print
/// back after minting.</summary>
public sealed class InviteDetails
{
    public Guid Id { get; set; }
    public Guid MinterNodeId { get; set; }
    public string MinterOwnerFingerprint { get; set; } = string.Empty;
    public InviteClaimKind Claim { get; set; } = InviteClaimKind.Unknown;
    public ProjectMemberRole? Role { get; set; }
    public Guid? ProjectId { get; set; }
    public string? ProjectRepositoryPath { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public DateTimeOffset MintedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Spent { get; set; }
    public DateTimeOffset? SpentAt { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    public string? ClaimedByRootFingerprint { get; set; }

    /// <summary>The secret itself is never on this read side — only the minting node's own local
    /// event store carries it (<see cref="InviteAggregate.Secret"/>), read back through the event
    /// stream directly by whatever needs to recompute a proof, never through this projection.</summary>
}

public sealed class InviteDetailsProjection : SingleStreamProjection<InviteDetails, Guid>
{
    public InviteDetails Create(IEvent<InviteMinted> @event) => new()
    {
        Id = @event.Data.InviteId,
        MinterNodeId = @event.Data.MinterNodeId,
        MinterOwnerFingerprint = @event.Data.MinterOwnerFingerprint,
        Claim = @event.Data.Claim,
        Role = @event.Data.Role,
        ProjectId = @event.Data.ProjectId,
        ProjectRepositoryPath = @event.Data.ProjectRepositoryPath,
        SecretHash = @event.Data.SecretHash,
        MintedAt = @event.Data.MintedAt,
        ExpiresAt = @event.Data.ExpiresAt,
    };

    public void Apply(IEvent<InviteSpent> @event, InviteDetails view)
    {
        view.Spent = true;
        view.SpentAt = @event.Data.SpentAt;
        view.ClaimedByNodeId = @event.Data.ClaimedByNodeId;
        view.ClaimedByRootFingerprint = @event.Data.ClaimedByRootFingerprint;
    }
}
