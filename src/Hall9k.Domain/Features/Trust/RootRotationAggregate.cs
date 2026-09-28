namespace Hall9k.Domain.Features.Trust;

/// <summary>One project's own record of one root-key rotation, keyed by
/// <see cref="RootRotationStreamId.For"/> — folds every sighting of the identical rotation into a
/// single standing fact rather than one row per sweep tick that observed it.</summary>
public sealed class RootRotationAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string RootFingerprint { get; private set; } = string.Empty;
    public Guid PromotedNodeId { get; private set; }
    public DateTimeOffset FirstObservedAt { get; private set; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public bool Revoked { get; private set; }
    public Guid? RevokedByNodeId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public void Apply(RootRotationObserved @event)
    {
        Id = RootRotationStreamId.For(@event.ProjectId, @event.RootFingerprint, @event.PromotedNodeId);
        ProjectId = @event.ProjectId;
        RootFingerprint = @event.RootFingerprint;
        PromotedNodeId = @event.PromotedNodeId;
        if (FirstObservedAt == default)
        {
            FirstObservedAt = @event.At;
        }

        LastObservedAt = @event.At;
        Revoked = false;
        RevokedByNodeId = null;
        RevokedAt = null;
    }

    public void Apply(RootRotationRevoked @event)
    {
        Revoked = true;
        RevokedByNodeId = @event.RevokedByNodeId;
        RevokedAt = @event.At;
    }
}
