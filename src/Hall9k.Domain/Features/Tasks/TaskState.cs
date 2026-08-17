using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>The work lifecycle. Execution detail lives on RunState (TASK-MODEL.md §2).
/// NeedsRefinement is reserved for the funnel era and deliberately absent in v0.</summary>
[JsonConverter(typeof(TaskStateJsonConverter))]
public sealed record TaskState
{
    public static readonly TaskState Queued = new("Queued");
    public static readonly TaskState Claimed = new("Claimed");
    public static readonly TaskState NeedsHuman = new("NeedsHuman");
    public static readonly TaskState Done = new("Done");
    public static readonly TaskState Failed = new("Failed");
    public static readonly TaskState Abandoned = new("Abandoned");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskState Unknown = new("");

    public string Value { get; }

    private TaskState(string value) => Value = value;

    public bool IsTerminal => this == Done || this == Failed || this == Abandoned;

    public static implicit operator string(TaskState? value) => value?.Value ?? string.Empty;

    public static implicit operator TaskState(string? value) => value.IsBlank() ? Unknown : new TaskState(value);

    public bool Equals(TaskState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TaskStateJsonConverter : JsonConverter<TaskState>
    {
        public override TaskState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TaskState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
