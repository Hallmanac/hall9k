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

    /// <summary>The invite's own ledger expiry when the hold was recorded; null on a hold recorded
    /// before that was carried.</summary>
    public DateTimeOffset? InviteExpiresAt { get; set; }

    /// <summary>The wait a hold recorded without <see cref="InviteExpiresAt"/> was given.</summary>
    public static readonly TimeSpan LegacyWait = TimeSpan.FromMinutes(10);

    /// <summary>When this hold stops waiting for approval: the invite's own expiry, or ten minutes
    /// after <see cref="HeldAt"/> for a hold that predates the field. <c>h9k status</c> prints it on
    /// both ends of the exchange and the daemon expires the hold once it has passed.</summary>
    public DateTimeOffset WaitEndsAt => InviteExpiresAt ?? HeldAt + LegacyWait;
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
        InviteExpiresAt = @event.Data.InviteExpiresAt,
    };

    public void Apply(IEvent<OwnerActApproved> @event, OwnerActHoldDetails view)
    {
        view.Approved = true;
        view.CommitId = @event.Data.CommitId;
    }

    public void Apply(IEvent<OwnerActHoldExpired> @event, OwnerActHoldDetails view) => view.Expired = true;
}
