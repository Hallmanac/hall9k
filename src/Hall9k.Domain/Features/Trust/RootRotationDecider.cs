using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Trust;

public static class RootRotationDecider
{
    public static RootRotationObserved Observe(Guid projectId, string rootFingerprint, Guid promotedNodeId, DateTimeOffset at)
    {
        if (rootFingerprint.IsBlank())
        {
            throw new DomainValidationException("Observing a root-key rotation needs the owner root it rotated.");
        }

        return new RootRotationObserved(projectId, rootFingerprint, promotedNodeId, at);
    }

    /// <summary>A sweep whose trust chain read no longer finds this stream's own rotation among the
    /// owner's live root keys — an earlier-ranked key voided it.</summary>
    public static RootRotationRevoked Revoke(
        Guid projectId, string rootFingerprint, Guid promotedNodeId, Guid? revokedByNodeId, DateTimeOffset at) =>
        new(projectId, rootFingerprint, promotedNodeId, revokedByNodeId, at);
}
