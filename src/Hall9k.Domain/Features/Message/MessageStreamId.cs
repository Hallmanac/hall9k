using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// Deterministic Marten stream ids for the message feature's two composite-keyed streams, never
/// <see cref="Guid.NewGuid"/> (AGENTS.md: domain ids are UUIDv7 via <c>DomainId</c>, but a stream
/// id here is not a new identity — it is a stable address derived from a key that already exists).
/// Never a node's own id reused directly either: that Guid already addresses the <c>Node</c>
/// feature's own identity stream, so reusing it for a message or inbox stream would collide with it
/// rather than opening a new one. Each id is a SHA-256 hash of a tagged, delimited key, taken
/// sixteen bytes wide — not a real UUID by RFC shape, only a Guid-sized value Marten can key a
/// stream by — so the identical (sender, seq) or sender always resolves to the identical stream.
/// </summary>
public static class MessageStreamId
{
    /// <summary>The per-message stream: one sender node plus the seq it used, per idea 202383dc's
    /// own key for the message aggregate.</summary>
    public static Guid ForMessage(Guid fromNodeId, long seq) =>
        Derive("hall9k-message", fromNodeId.ToString("N"), seq.ToString(CultureInfo.InvariantCulture));

    /// <summary>The per-sender inbox stream: this node's own cursor and vouch status for one
    /// sender's outbox, kept in this node's own store, never the ledger.</summary>
    public static Guid ForInbox(Guid senderNodeId) =>
        Derive("hall9k-message-inbox", senderNodeId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
