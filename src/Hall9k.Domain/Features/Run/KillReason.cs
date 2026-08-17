using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

[JsonConverter(typeof(KillReasonJsonConverter))]
public sealed record KillReason
{
    public static readonly KillReason BudgetExceeded = new("BudgetExceeded");
    public static readonly KillReason HumanRequested = new("HumanRequested");
    public static readonly KillReason Superseded = new("Superseded");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly KillReason Unknown = new("");

    public string Value { get; }

    private KillReason(string value) => Value = value;

    public static implicit operator string(KillReason? value) => value?.Value ?? string.Empty;

    public static implicit operator KillReason(string? value) => value.IsBlank() ? Unknown : new KillReason(value);

    public bool Equals(KillReason? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class KillReasonJsonConverter : JsonConverter<KillReason>
    {
        public override KillReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, KillReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
