using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// How a task note came to be recorded: typed through <c>h9k task note</c>, or read by the daemon
/// out of a work session's closing block. A closed vocabulary, so a sealed record with static
/// instances (TASK-MODEL.md §8). It is persisted on every note event, which is why it carries a
/// JSON converter, unlike <c>LessonProvenanceMark</c>, which is only derived.
/// </summary>
[JsonConverter(typeof(TaskNoteSourceJsonConverter))]
public sealed record TaskNoteSource
{
    /// <summary>Recorded by <c>h9k task note</c>, from a terminal or from inside a session.</summary>
    public static readonly TaskNoteSource Verb = new("Verb", "h9k task note");

    /// <summary>Read by the daemon out of the closing block of a work session's final message.</summary>
    public static readonly TaskNoteSource ClosingBlock = new("ClosingBlock", "closing block");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskNoteSource Unknown = new("", "unknown source");

    public string Value { get; }

    /// <summary>The words a rendered list uses for this source.</summary>
    public string Label { get; }

    private TaskNoteSource(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public static implicit operator string(TaskNoteSource? source) => source?.Value ?? string.Empty;

    public static implicit operator TaskNoteSource(string? value) => value switch
    {
        "Verb" => Verb,
        "ClosingBlock" => ClosingBlock,
        _ => Unknown,
    };

    public bool Equals(TaskNoteSource? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TaskNoteSourceJsonConverter : JsonConverter<TaskNoteSource>
    {
        public override TaskNoteSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TaskNoteSource value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
