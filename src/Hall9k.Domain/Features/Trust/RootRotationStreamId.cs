using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// The deterministic Marten stream id for one project's own record of one root-key rotation, the
/// same reasoning <see cref="UnverifiedLedgerWriteStreamId"/> already applies: never
/// <see cref="Guid.NewGuid"/>, a stable address derived from a key that already exists (the
/// project, the owner root, and the node id the rotation promoted) so the identical rotation
/// observed on a later sweep folds into the same stream rather than opening a new one every tick.
/// </summary>
public static class RootRotationStreamId
{
    public static Guid For(Guid projectId, string rootFingerprint, Guid promotedNodeId) =>
        Derive("hall9k-root-rotation", projectId.ToString("N"), rootFingerprint, promotedNodeId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
