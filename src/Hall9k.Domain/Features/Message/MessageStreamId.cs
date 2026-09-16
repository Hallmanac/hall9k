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
/// stream by — so the identical (sender, project, seq) or (sender, project) always resolves to the
/// identical stream.
/// <para>
/// <c>projectId</c> throughout is this INSTALL's own local <see cref="Hall9k.Domain.Features.Project.Projections.ProjectDetails.Id"/>
/// (idea 202383dc, M2's project-scoped outbox) — never the ledger-derived wire project key an
/// envelope itself carries. A stream id is purely local storage plumbing, read and written only by
/// the one install whose own database it lives in, so there is no cross-node agreement to keep: the
/// sender's own queue-then-flush and a receiver's own read each resolve their OWN local project id
/// independently, and the one case where the two sides must actually agree — a "project"-addressed
/// envelope a node reads back from its own outbox — already uses the identical local id on both
/// sides, since <c>MessageSweepEngine</c> flushes and reads the same project within the same sweep.
/// </para>
/// </summary>
public static class MessageStreamId
{
    /// <summary>The per-message stream: one sender node, the local project it was queued or
    /// received under, and the seq the sender used, per idea 202383dc's own key for the message
    /// aggregate — extended by M2 so two local projects sharing a sender never collide.</summary>
    public static Guid ForMessage(Guid fromNodeId, Guid projectId, long seq) =>
        Derive(
            "hall9k-message", fromNodeId.ToString("N"), projectId.ToString("N"),
            seq.ToString(CultureInfo.InvariantCulture));

    /// <summary>The per-sender, per-project inbox stream: this node's own cursor and vouch status
    /// for one sender's outbox, scoped to one local project — kept in this node's own store, never
    /// the ledger. Reading one project's copy of a sender's outbox never touches another project's
    /// own cursor for that identical sender (idea 202383dc, M2).</summary>
    public static Guid ForInbox(Guid senderNodeId, Guid projectId) =>
        Derive("hall9k-message-inbox", senderNodeId.ToString("N"), projectId.ToString("N"));

    /// <summary>The pre-M2 shape of <see cref="ForInbox"/>, before a local project id was ever part
    /// of the key — never used to read or write a live cursor going forward, only by
    /// <c>Hall9k.Connectors.Messaging.MessageInbox.ReadFromAsync</c>'s own migration fallback
    /// (<c>Hall9k.Connectors.Messaging.LegacyMessageAdoption</c>) to find a cursor a node may have
    /// advanced before this change, so the adopting project's first per-project read of a sender does
    /// not re-read and re-store content it already handled under the old, unscoped stream
    /// (independent pre-PR review, cycle 1, conformance lens, medium).
    /// </summary>
    public static Guid ForInboxBeforeProjectScoping(Guid senderNodeId) =>
        Derive("hall9k-message-inbox", senderNodeId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
