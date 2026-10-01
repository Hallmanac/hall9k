namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Who is reading the feed: this node's own identity, which is what tells a teammate's activity
/// from the reader's own. <see cref="OwnerRootFingerprint"/> is the root card C's ownership rule
/// compares a task's owner against; <see cref="OwnerId"/> and <see cref="NodeId"/> are what a take
/// refusal names its requester by, since that event carries no root. A null root (this node's owner
/// has not claimed one) means no comparison can be made, so nothing is filtered, the same reading
/// the board gives it. A plain class, never a record, for the reason <see cref="Trust.MemberLabelLookup"/>
/// is one: <c>EventScopeRegistryTests</c> would otherwise mistake it for an event.
/// </summary>
public sealed class OrchestratorFeedViewer(string? ownerRootFingerprint, Guid? ownerId, Guid? nodeId)
{
    public string? OwnerRootFingerprint { get; } = ownerRootFingerprint;

    public Guid? OwnerId { get; } = ownerId;

    public Guid? NodeId { get; } = nodeId;
}
