using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// One event's own true origin — the preserved <see cref="ReplicationEventHeaders"/> when this is
/// itself a fact this node received by replication (never this node's own local event id or
/// sequence, which would silently overwrite the real origin the moment this node forwards it a
/// second hop), falling back to this node's own stamped identity
/// (<see cref="EventOriginStampingListener"/>) for an event this node genuinely produced. Shared by
/// <see cref="EventCatchUpResponder"/> (forwarding any project-scoped event this node holds, own or
/// already-replicated, to a catch-up requester) and <see cref="EventReplicationOutbox"/>'s own
/// scope-change resend pass (idea 8c5993c5), which needs the identical own-or-replicated origin
/// preservation: a resend run from a fleet sibling that only ever received a stream by replication
/// must still be able to resend its full history under that stream's own true origin.
/// </summary>
public static class ReplicationEventOriginResolver
{
    public static (Guid OriginNodeId, string OriginOwnerRootFingerprint, Guid OriginEventId, long OriginSequence) Resolve(
        IEvent candidate, Guid myNodeId, string myOwnerFingerprint)
    {
        string? originNodeIdText = candidate.GetHeader(ReplicationEventHeaders.OriginNodeId) as string;
        if (originNodeIdText.IsNotBlank() && Guid.TryParse(originNodeIdText, out Guid originNodeId))
        {
            string originHeaderValue = candidate.GetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint) as string ?? string.Empty;
            string originOwnerRootFingerprint = originHeaderValue.IsNotBlank() ? originHeaderValue : myOwnerFingerprint;
            Guid originEventId = Guid.TryParse(
                candidate.GetHeader(ReplicationEventHeaders.OriginEventId) as string, out Guid parsedEventId)
                ? parsedEventId
                : candidate.Id;
            long originSequence = long.TryParse(
                candidate.GetHeader(ReplicationEventHeaders.OriginSequence) as string, out long parsedSequence)
                ? parsedSequence
                : candidate.Sequence;
            return (originNodeId, originOwnerRootFingerprint, originEventId, originSequence);
        }

        string ownNodeIdText = candidate.GetHeader(EventOriginStampingListener.NodeIdHeader) as string ?? string.Empty;
        Guid ownNodeId = Guid.TryParse(ownNodeIdText, out Guid parsedOwnNodeId) ? parsedOwnNodeId : myNodeId;
        // string.Empty (never null) is EventOriginStampingListener.UnclaimedOwnerRootFingerprint —
        // that listener's own doc says to resolve or fall back rather than trust it as a final
        // answer, so it is read here the identical way EventReplicationOutbox.AudienceFor treats it.
        string ownHeaderValue = candidate.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string ?? string.Empty;
        string ownOwnerRootFingerprint = ownHeaderValue.IsNotBlank() ? ownHeaderValue : myOwnerFingerprint;
        return (ownNodeId, ownOwnerRootFingerprint, candidate.Id, candidate.Sequence);
    }
}
