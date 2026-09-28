using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// <c>h9k status</c>'s own read view of one project's root-key rotations (idea 6be68ee2, PR B) —
/// written only by <c>Hall9k.Daemon.Messaging.MessageSweepEngine</c>, off the same trust chain its
/// own sweep already computes once per tick, never by a live ledger walk from <c>h9k status</c>
/// itself.
/// </summary>
public sealed class RootRotationDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string RootFingerprint { get; set; } = string.Empty;
    public Guid PromotedNodeId { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool Revoked { get; set; }
    public Guid? RevokedByNodeId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed partial class RootRotationDetailsProjection : SingleStreamProjection<RootRotationDetails, Guid>
{
    public RootRotationDetails Create(IEvent<RootRotationObserved> @event) => new()
    {
        Id = @event.StreamId,
        ProjectId = @event.Data.ProjectId,
        RootFingerprint = @event.Data.RootFingerprint,
        PromotedNodeId = @event.Data.PromotedNodeId,
        FirstObservedAt = @event.Data.At,
        LastObservedAt = @event.Data.At,
    };

    public void Apply(IEvent<RootRotationObserved> @event, RootRotationDetails view)
    {
        view.LastObservedAt = @event.Data.At;
        view.Revoked = false;
        view.RevokedByNodeId = null;
        view.RevokedAt = null;
    }

    public void Apply(IEvent<RootRotationRevoked> @event, RootRotationDetails view)
    {
        view.Revoked = true;
        view.RevokedByNodeId = @event.Data.RevokedByNodeId;
        view.RevokedAt = @event.Data.At;
    }
}
