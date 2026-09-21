using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// Where a decision stands (idea d805fd8b, piece 1). Two states and a sentinel, because a
/// decision has exactly one ending: something replaced it, or overruled it, and that ending is
/// always an explicit act carrying a reason. Nothing deletes — a superseded decision stays
/// queryable forever, which is the whole reason the log became platform data rather than a
/// markdown file somebody edits.
/// </summary>
[JsonConverter(typeof(DecisionStatusJsonConverter))]
public sealed record DecisionStatus
{
    /// <summary>Recorded and binding.</summary>
    public static readonly DecisionStatus Recorded = new("Recorded");

    /// <summary>Replaced or overruled, with a reason and (when there is one) the decision that replaced it. Terminal.</summary>
    public static readonly DecisionStatus Superseded = new("Superseded");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly DecisionStatus Unknown = new("");

    public string Value { get; }

    private DecisionStatus(string value) => Value = value;

    public static implicit operator string(DecisionStatus? status) => status?.Value ?? string.Empty;

    public static implicit operator DecisionStatus(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : new DecisionStatus(value);

    public bool Equals(DecisionStatus? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class DecisionStatusJsonConverter : JsonConverter<DecisionStatus>
    {
        public override DecisionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, DecisionStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
