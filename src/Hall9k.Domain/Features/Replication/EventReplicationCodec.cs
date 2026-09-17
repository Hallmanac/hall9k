using System.Text.Json;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Encodes and decodes the batch an <c>events</c>-kind <c>MessageEnvelopeV1</c> carries as its own
/// body (idea 202383dc, M2a) — a JSON array of <see cref="ReplicatedEventRecord"/>, each one a
/// single project-scoped event this node appended, past the sender's own switch-on point. Nested
/// here rather than top-level, the identical reason <c>MessageEnvelopeCodec</c>'s own DTOs are
/// nested: a reflection scan for real Marten event types (<c>EventScopeRegistryTests</c>) must
/// never mistake this shape for one that needs classifying — nothing here is ever appended to a
/// stream on its own; <see cref="ReplicatedEventRecord.EventDataJson"/> is what gets appended, once
/// decoded back into its own real event type.
/// </summary>
public static class EventReplicationCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One replicated event, wire shape. <see cref="EventTypeName"/> is the appending node's own
    /// <see cref="Type.FullName"/> for the event — resolved back to a real .NET type on the
    /// receiving side via every type <see cref="Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry.KnownEventTypes"/>
    /// names, never a bare assembly-qualified name a receiving build's own assembly version could reject.
    /// <see cref="OriginEventId"/> is the origin's own Marten event id — the identity a receiver
    /// dedupes on, since local version numbers are local to each node's own store.
    /// <see cref="OriginSequence"/> is the origin's own global sequence, kept so a receiver's own
    /// origin metadata (idea 202383dc's own list: "owner root, node, event id, origin sequence")
    /// is complete without a second round trip to the sender.
    /// <see cref="OriginProjectId"/> is the sending node's own LOCAL project id for this event — a
    /// per-install coordinate, never a shared identity (the ledger repository is) — carried so a
    /// forwarded copy (M2b, not built yet) can be rewritten again at each hop; trailing and
    /// defaulted so an envelope from a sender on an older build still decodes. Never itself applied:
    /// the receiver's own local project id, not this one, is what a replicated event's project
    /// coordinate is rewritten to on apply.
    /// </summary>
    public sealed record ReplicatedEventRecord(
        Guid StreamId,
        string EventTypeName,
        string EventDataJson,
        Guid OriginEventId,
        long OriginSequence,
        Guid OriginNodeId,
        string OriginOwnerRootFingerprint,
        DateTimeOffset OriginAt,
        Guid OriginProjectId = default);

    public static string EncodeBatch(IReadOnlyList<ReplicatedEventRecord> records) =>
        JsonSerializer.Serialize(records, Options);

    /// <summary>Null on anything that fails to parse — idea 202383dc's own "an unknown kind is
    /// stored and skipped, never refused" extends to a malformed batch body: one bad envelope from
    /// an otherwise-vouched sender must never stop that sender's inbox from ever advancing again.</summary>
    public static IReadOnlyList<ReplicatedEventRecord>? DecodeBatch(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ReplicatedEventRecord>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One catch-up ask (idea 202383dc, M2b, task 9408d525) — the body of a
    /// <see cref="Hall9k.Domain.Features.Message.MessageKind.EventsRequest"/> envelope. Exactly one
    /// of three shapes: <see cref="ForStreamId"/> set asks for one specific stream's own events,
    /// whoever originated them (the ledger-record adoption path — task add's own missing-stream
    /// case); <see cref="ForOriginNodeId"/> set (with <see cref="ForStreamId"/> null) asks for
    /// everything a peer holds from that one origin node past <see cref="SinceOriginSequence"/> — a
    /// coarse, safe lower bound, since the origin's own global sequence is not contiguous across
    /// projects and node/owner-scoped events, so "greater than" is always a superset of what is
    /// genuinely missing, never a subset, and any overlap a peer re-sends is harmless (dedupe by
    /// origin event id); both null asks for everything the peer holds for the project at all — a
    /// brand-new node's own bootstrap.
    /// </summary>
    public sealed record EventsRequestRecord(Guid RequestId, Guid? ForOriginNodeId, long SinceOriginSequence, Guid? ForStreamId);

    public static string EncodeRequest(EventsRequestRecord request) => JsonSerializer.Serialize(request, Options);

    public static EventsRequestRecord? DecodeRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EventsRequestRecord>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A peer's own answer that it cannot satisfy an <see cref="EventsRequestRecord"/> at all — the
    /// body of a <see cref="Hall9k.Domain.Features.Message.MessageKind.EventsUnavailable"/> envelope
    /// (idea 202383dc, M2b: "a peer that cannot answer says so").
    /// </summary>
    public sealed record EventsUnavailableRecord(Guid RequestId, string Reason);

    public static string EncodeUnavailable(EventsUnavailableRecord unavailable) => JsonSerializer.Serialize(unavailable, Options);

    public static EventsUnavailableRecord? DecodeUnavailable(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EventsUnavailableRecord>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
