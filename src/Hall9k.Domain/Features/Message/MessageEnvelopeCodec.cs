using System.Text.Json;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// Encodes and decodes the version-1 envelope to and from the JSON one outbox file
/// (<c>messages/&lt;seq&gt;.json</c>) actually holds. <see cref="Decode"/> checks the mandatory
/// <c>version</c> field before touching anything else: an unsupported version is refused —
/// reported back rather than thrown — so the caller can log it and move on to the next envelope,
/// per idea 202383dc's own rule that neither an unknown version nor an unknown kind ever fails the
/// reader. A version-1 envelope that is otherwise malformed (truncated JSON, a non-object root, a
/// non-integer version, an unroutable <c>to</c>) is refused the same way, as
/// <see cref="DecodeOutcome.Malformed"/>, rather than thrown — one bad envelope from an otherwise
/// vouched sender must never stop that sender's inbox from ever advancing again.
/// <see cref="DecodeResult"/> and its nested DTO stay nested here rather than top-level in
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
        Malformed,
    }

    public sealed record DecodeResult(DecodeOutcome Outcome, MessageEnvelopeV1? Envelope, int? Version)
    {
        public static DecodeResult Parsed(MessageEnvelopeV1 envelope) =>
            new(DecodeOutcome.Parsed, envelope, MessageEnvelopeV1.Version);

        public static DecodeResult UnsupportedVersion(int? version) =>
            new(DecodeOutcome.UnsupportedVersion, null, version);

        public static DecodeResult Malformed() =>
            new(DecodeOutcome.Malformed, null, null);
    }

    private sealed record EnvelopeDto(
        int Version,
        long Seq,
        DateTimeOffset At,
        Guid FromNode,
        string FromOwner,
        string? To,
        string? About,
        string? Kind,
        string Body,
        string? ProjectKey = null);

    public static string Encode(MessageEnvelopeV1 envelope)
    {
        EnvelopeDto dto = new(
            MessageEnvelopeV1.Version, envelope.Seq, envelope.At, envelope.FromNode, envelope.FromOwner,
            envelope.To.Value, envelope.About, envelope.Kind.Value, envelope.Body, envelope.ProjectKey);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static DecodeResult Decode(string json)
    {
        try
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

            if (dto.To is null
                || dto.Seq < 1
                || dto.At == default
                || dto.FromNode == Guid.Empty
                || dto.FromOwner.IsBlank()
                || dto.Kind is null
                || dto.Body is null)
            {
                return DecodeResult.Malformed();
            }

            MessageEnvelopeV1 envelope = new(
                dto.Seq, dto.At, dto.FromNode, dto.FromOwner,
                MessageAudience.Parse(dto.To), dto.About, MessageKind.Parse(dto.Kind ?? string.Empty), dto.Body,
                dto.ProjectKey);
            return DecodeResult.Parsed(envelope);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException or DomainValidationException)
        {
            return DecodeResult.Malformed();
        }
    }
}
