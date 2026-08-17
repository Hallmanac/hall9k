using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

[JsonConverter(typeof(RequeueReasonJsonConverter))]
public sealed record RequeueReason
{
    public static readonly RequeueReason LeaseExpired = new("LeaseExpired");
    public static readonly RequeueReason RunFailedRetryable = new("RunFailedRetryable");
    public static readonly RequeueReason HumanRequested = new("HumanRequested");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly RequeueReason Unknown = new("");

    public string Value { get; }

    private RequeueReason(string value) => Value = value;

    public static implicit operator string(RequeueReason? value) => value?.Value ?? string.Empty;

    public static implicit operator RequeueReason(string? value) => value.IsBlank() ? Unknown : new RequeueReason(value);

    public bool Equals(RequeueReason? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RequeueReasonJsonConverter : JsonConverter<RequeueReason>
    {
        public override RequeueReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RequeueReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
