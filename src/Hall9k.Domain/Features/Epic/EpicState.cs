using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// An epic's whole lifecycle, deliberately two words wide: an epic is open until a human
/// closes it, and nothing else moves it — not its last member task closing out, not every
/// member leaving. Closing is always an explicit act with a reason (Decisions Log #99): the
/// standing never-auto-close doctrine applies here exactly as it does to a task.
/// </summary>
[JsonConverter(typeof(EpicStateJsonConverter))]
public sealed record EpicState
{
    /// <summary>Named and tracking member tasks. The only state a task can join.</summary>
    public static readonly EpicState Open = new("Open");

    /// <summary>Closed by an explicit human act, with a reason (terminal).</summary>
    public static readonly EpicState Closed = new("Closed");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly EpicState Unknown = new("");

    public string Value { get; }

    private EpicState(string value) => Value = value;

    public static implicit operator string(EpicState? value) => value?.Value ?? string.Empty;

    public static implicit operator EpicState(string? value) => value.IsBlank() ? Unknown : new EpicState(value);

    public bool Equals(EpicState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class EpicStateJsonConverter : JsonConverter<EpicState>
    {
        public override EpicState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, EpicState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
