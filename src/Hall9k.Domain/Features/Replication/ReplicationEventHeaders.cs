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
    public const string ReceivedFromNodeId = "receivedFromNodeId";
    public const string ReceivedAt = "receivedAt";
}
