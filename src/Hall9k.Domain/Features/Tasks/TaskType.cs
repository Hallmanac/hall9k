using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>Drives persona, prompt template, and verification profile (PLAN.md §4).</summary>
[JsonConverter(typeof(TaskTypeJsonConverter))]
public sealed record TaskType
{
    public static readonly TaskType Feature = new("Feature");
    public static readonly TaskType Bugfix = new("Bugfix");
    public static readonly TaskType Refactor = new("Refactor");
    public static readonly TaskType Chore = new("Chore");
    public static readonly TaskType Research = new("Research");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskType Unknown = new("");

    public string Value { get; }

    private TaskType(string value) => Value = value;

    public static implicit operator string(TaskType? value) => value?.Value ?? string.Empty;

    public static implicit operator TaskType(string? value) => value.IsBlank() ? Unknown : new TaskType(value);

    public bool Equals(TaskType? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TaskTypeJsonConverter : JsonConverter<TaskType>
    {
        public override TaskType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TaskType value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
