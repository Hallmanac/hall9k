using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Deterministic keys for this feature's own bookkeeping documents (idea 202383dc, M2a) — the
/// identical <c>MessageStreamId</c> idiom: a SHA-256 hash of a tagged, delimited key, never
/// <see cref="Guid.NewGuid"/>, so the identical (sender, project) or (origin event id) always
/// resolves to the identical document id.
/// </summary>
public static class EventReplicationStreamId
{
    /// <summary>This node's own progress applying one sender's events-kind envelopes for one local
    /// project — <see cref="EventReplicationInboxCursor"/>'s own id.</summary>
    public static Guid ForInboxCursor(Guid senderNodeId, Guid projectId) =>
        Derive("hall9k-event-replication-inbox", senderNodeId.ToString("N"), projectId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
