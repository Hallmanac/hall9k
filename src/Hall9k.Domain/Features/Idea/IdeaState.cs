using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Where an idea is in DISCOVERY (Decisions Log #35). An idea asks "what is this?"; a draft
/// task asks "how does this become executable?" — so the vocabulary here is deliberately not
/// the task lifecycle's. There is no "refined" state: refinement belongs to the draft the
/// idea is promoted into, and the idea's story ends the moment it becomes one.
/// </summary>
[JsonConverter(typeof(IdeaStateJsonConverter))]
public sealed record IdeaState
{
    /// <summary>Written down and in discovery: revisable, assignable to a project, promotable.</summary>
    public static readonly IdeaState Captured = new("Captured");

    /// <summary>It became a draft task; the task's stream carries the story from here (terminal).</summary>
    public static readonly IdeaState Promoted = new("Promoted");

    /// <summary>Closed with a recorded reason. Never deleted — a discarded idea is history, not an absence (terminal).</summary>
    public static readonly IdeaState Discarded = new("Discarded");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly IdeaState Unknown = new("");

    public string Value { get; }

    private IdeaState(string value) => Value = value;

    /// <summary>Promoted and Discarded are both endings; only a captured idea still moves.</summary>
    public bool IsTerminal => this == Promoted || this == Discarded;

    public static implicit operator string(IdeaState? value) => value?.Value ?? string.Empty;

    public static implicit operator IdeaState(string? value) => value.IsBlank() ? Unknown : new IdeaState(value);

    public bool Equals(IdeaState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class IdeaStateJsonConverter : JsonConverter<IdeaState>
    {
        public override IdeaState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, IdeaState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
