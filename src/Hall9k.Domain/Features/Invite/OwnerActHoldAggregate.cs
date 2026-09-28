using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

public sealed class OwnerActHoldAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ProjectRepositoryPath { get; private set; } = string.Empty;
    public Guid InviteId { get; private set; }
    public Guid RequesterNodeId { get; private set; }
    public string CandidateOwnerFingerprint { get; private set; } = string.Empty;
    public ProjectMemberRole Role { get; private set; } = ProjectMemberRole.Unknown;
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset HeldAt { get; private set; }
    public bool Approved { get; private set; }
    public bool Expired { get; private set; }

    public void Apply(OwnerActHeld @event)
    {
        Id = @event.Id;
        ProjectId = @event.ProjectId;
        ProjectRepositoryPath = @event.ProjectRepositoryPath;
        InviteId = @event.InviteId;
        RequesterNodeId = @event.RequesterNodeId;
        CandidateOwnerFingerprint = @event.CandidateOwnerFingerprint;
        Role = @event.Role;
        IssuedAt = @event.IssuedAt;
        HeldAt = @event.HeldAt;
    }

    public void Apply(OwnerActApproved @event) => Approved = true;

    public void Apply(OwnerActHoldExpired @event) => Expired = true;
}
