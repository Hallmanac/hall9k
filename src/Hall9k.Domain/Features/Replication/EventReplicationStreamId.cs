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

    /// <summary>This node's own progress reading one sender's events-request/events-unavailable
    /// envelopes for one local project (idea 202383dc, M2b) — <see cref="EventCatchUpInboxCursor"/>'s
    /// own id, a separate cursor from <see cref="ForInboxCursor"/> so this second, independent read
    /// of the identical outbox ref never disturbs the events-replication cursor's own position.</summary>
    public static Guid ForCatchUpInboxCursor(Guid senderNodeId, Guid projectId) =>
        Derive("hall9k-event-catchup-inbox", senderNodeId.ToString("N"), projectId.ToString("N"));

    /// <summary>This node's own highest applied global sequence from one origin node, for one local
    /// project (idea 202383dc, M2b) — <see cref="EventOriginProgress"/>'s own id, the coarse "since"
    /// bound an outbound gap-fill events-request is built from.</summary>
    public static Guid ForOriginProgress(Guid projectId, Guid originNodeId) =>
        Derive("hall9k-event-origin-progress", projectId.ToString("N"), originNodeId.ToString("N"));

    /// <summary>One fleet sibling's own reconcile of one project with this node (task 252bc5cf) —
    /// <see cref="FleetProjectReconcile"/>'s own id. Derived from the pair rather than minted fresh
    /// because the document's own existence is the guard that keeps the sweep from asking the same
    /// peer twice and keeps two nodes from asking each other forever.</summary>
    public static Guid ForFleetReconcile(Guid peerNodeId, Guid projectId) =>
        Derive("hall9k-fleet-project-reconcile", peerNodeId.ToString("N"), projectId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
