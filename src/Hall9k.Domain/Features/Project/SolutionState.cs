using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// A solution's whole lifecycle, two words wide, the <see cref="Epic.EpicState"/> shape: a solution
/// is open until a human closes it with a reason, and nothing else moves it, not every member being
/// archived and not its last task closing out. Only an <see cref="Open"/> membership holds the
/// project it sits on to that one solution; a <see cref="Closed"/> one does not.
/// </summary>
[JsonConverter(typeof(SolutionStateJsonConverter))]
public sealed record SolutionState
{
    /// <summary>Grouping its member projects, and holding each to this one solution.</summary>
    public static readonly SolutionState Open = new("Open");

    /// <summary>Closed by an explicit human act, with a reason (terminal); it no longer holds its members.</summary>
    public static readonly SolutionState Closed = new("Closed");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly SolutionState Unknown = new("");

    public string Value { get; }

    private SolutionState(string value) => Value = value;

    public static implicit operator string(SolutionState? value) => value?.Value ?? string.Empty;

    public static implicit operator SolutionState(string? value) => value.IsBlank() ? Unknown : new SolutionState(value);

    public bool Equals(SolutionState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class SolutionStateJsonConverter : JsonConverter<SolutionState>
    {
        public override SolutionState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, SolutionState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
