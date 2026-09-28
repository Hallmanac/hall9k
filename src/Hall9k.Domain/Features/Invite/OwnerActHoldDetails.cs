using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Invite;

/// <summary>Read side of <see cref="OwnerActHoldAggregate"/> - what <c>h9k status</c> and
/// <c>h9k project member approve</c> both read.</summary>
public sealed class OwnerActHoldDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectRepositoryPath { get; set; } = string.Empty;
    public Guid InviteId { get; set; }
    public Guid RequesterNodeId { get; set; }
    public string CandidateOwnerFingerprint { get; set; } = string.Empty;
    public ProjectMemberRole Role { get; set; } = ProjectMemberRole.Unknown;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset HeldAt { get; set; }
    public bool Approved { get; set; }
    public string? CommitId { get; set; }
    public bool Expired { get; set; }
}

public sealed partial class OwnerActHoldDetailsProjection : SingleStreamProjection<OwnerActHoldDetails, Guid>
{
    public OwnerActHoldDetails Create(IEvent<OwnerActHeld> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        ProjectRepositoryPath = @event.Data.ProjectRepositoryPath,
        InviteId = @event.Data.InviteId,
        RequesterNodeId = @event.Data.RequesterNodeId,
        CandidateOwnerFingerprint = @event.Data.CandidateOwnerFingerprint,
        Role = @event.Data.Role,
        IssuedAt = @event.Data.IssuedAt,
        HeldAt = @event.Data.HeldAt,
    };

    public void Apply(IEvent<OwnerActApproved> @event, OwnerActHoldDetails view)
    {
        view.Approved = true;
        view.CommitId = @event.Data.CommitId;
    }

    public void Apply(IEvent<OwnerActHoldExpired> @event, OwnerActHoldDetails view) => view.Expired = true;
}
