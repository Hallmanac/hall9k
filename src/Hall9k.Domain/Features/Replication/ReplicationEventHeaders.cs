namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Marten event header keys a replicated fact carries beside whatever
/// <c>EventOriginStampingListener</c> also stamps on it (idea 202383dc, M2a: "keeping origin
/// metadata (owner root, node, event id, origin sequence) and adding received-from and
/// received-at"). Deliberately distinct keys from <c>EventOriginStampingListener.NodeIdHeader</c>/
/// <c>OwnerRootFingerprintHeader</c>: that listener runs on every append regardless of origin and
/// would otherwise stamp the RECEIVING node's own identity over whatever this feature sets first,
/// since it always overwrites unconditionally. These keys are never touched by that listener, so a
/// replicated fact's true origin survives it.
/// </summary>
public static class ReplicationEventHeaders
{
    public const string OriginNodeId = "originNodeId";
    public const string OriginOwnerRootFingerprint = "originOwnerRootFingerprint";
    public const string OriginEventId = "originEventId";
    public const string OriginSequence = "originSequence";
    /// <summary>The sending node's own local project id for this event, at the moment it sent it —
    /// a per-install coordinate, never this event's shared identity (idea 202383dc, M2a follow-up:
    /// PLAN.md §16 #213). Kept so a future forwarded copy (M2b) can be rewritten
    /// again at each hop; this node's own applied copy is keyed by its OWN local project id, not
    /// this one.</summary>
    public const string OriginProjectId = "originProjectId";
    public const string ReceivedFromNodeId = "receivedFromNodeId";
    public const string ReceivedAt = "receivedAt";
}
