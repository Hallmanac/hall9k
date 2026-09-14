using System.Text.Json;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// Encodes and decodes the version-1 envelope to and from the JSON one outbox file
/// (<c>messages/&lt;seq&gt;.json</c>) actually holds. <see cref="Decode"/> checks the mandatory
/// <c>version</c> field before touching anything else: an unsupported version is refused —
/// reported back rather than thrown — so the caller can log it and move on to the next envelope,
/// per idea 202383dc's own rule that neither an unknown version nor an unknown kind ever fails the
/// reader. <see cref="DecodeResult"/> and its nested DTO stay nested here rather than top-level in
/// this feature's own flat namespace, so a reflection scan for real Marten event types (
/// <c>EventScopeRegistryTests</c>) never mistakes either one for an event that needs classifying —
/// nothing here is ever appended to a stream.
/// </summary>
public static class MessageEnvelopeCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public enum DecodeOutcome
    {
        Parsed,
        UnsupportedVersion,
    }

    public sealed record DecodeResult(DecodeOutcome Outcome, MessageEnvelopeV1? Envelope, int? Version)
    {
        public static DecodeResult Parsed(MessageEnvelopeV1 envelope) =>
            new(DecodeOutcome.Parsed, envelope, MessageEnvelopeV1.Version);

        public static DecodeResult UnsupportedVersion(int? version) =>
            new(DecodeOutcome.UnsupportedVersion, null, version);
    }

    private sealed record EnvelopeDto(
        int Version,
        long Seq,
        DateTimeOffset At,
        Guid FromNode,
        string FromOwner,
        string To,
        string? About,
        string Kind,
        string Body);

    public static string Encode(MessageEnvelopeV1 envelope)
    {
        EnvelopeDto dto = new(
            MessageEnvelopeV1.Version, envelope.Seq, envelope.At, envelope.FromNode, envelope.FromOwner,
            envelope.To.Value, envelope.About, envelope.Kind.Value, envelope.Body);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static DecodeResult Decode(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("version", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.Number)
        {
            return DecodeResult.UnsupportedVersion(null);
        }

        int version = versionElement.GetInt32();
        if (version != MessageEnvelopeV1.Version)
        {
            return DecodeResult.UnsupportedVersion(version);
        }

        EnvelopeDto dto = document.Deserialize<EnvelopeDto>(Options)
            ?? throw new JsonException("A version-1 envelope deserialized to null despite matching the version check.");

        MessageEnvelopeV1 envelope = new(
            dto.Seq, dto.At, dto.FromNode, dto.FromOwner,
            MessageAudience.Parse(dto.To), dto.About, MessageKind.Parse(dto.Kind), dto.Body);
        return DecodeResult.Parsed(envelope);
    }
}
