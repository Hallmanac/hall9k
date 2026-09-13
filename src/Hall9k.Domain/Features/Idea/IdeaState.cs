using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Where an idea is in DISCOVERY (Decisions Log #35). An idea asks "what is this?"; a draft
/// task asks "how does this become executable?" — so the vocabulary here is deliberately not
/// the task lifecycle's. There is no "refined" state: refinement belongs to the draft(s) the
/// idea fans out into, and cutting one never ends the idea's own story.
/// <para>
/// Exactly two terminal states, both explicit human acts (Brian, 2026-08-21): <see cref="Concluded"/>
/// — discovery happened and something came of it, tasks cut or an outcome acted on some other
/// way — and <see cref="Archived"/> — discovery happened and nothing came of it. The shipped 1:1
/// promote (<c>IdeaPromoted</c>) and discard (<c>IdeaDiscarded</c>) events are reconciled into
/// this same pair rather than kept as their own endings: a promotion always meant something came
/// of the idea, which is exactly what <see cref="Concluded"/> means now, and a discard always
/// meant nothing did, which is exactly <see cref="Archived"/>. Nothing rewrites the historical
/// events — they stay on the stream forever — but replaying them, or reading a document one of
/// them last wrote, lands on the same two states every idea reaches today.
/// </para>
/// </summary>
[JsonConverter(typeof(IdeaStateJsonConverter))]
public sealed record IdeaState
{
    /// <summary>Written down and in discovery: revisable, assignable to a project, cuttable into any number of tasks.</summary>
    public static readonly IdeaState Captured = new("Captured");

    /// <summary>Discovery happened and something came of it — tasks cut, or an outcome acted on (terminal).</summary>
    public static readonly IdeaState Concluded = new("Concluded");

    /// <summary>Discovery happened and nothing came of it. Never deleted — an archived idea is history, not an absence (terminal).</summary>
    public static readonly IdeaState Archived = new("Archived");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly IdeaState Unknown = new("");

    public string Value { get; }

    private IdeaState(string value) => Value = value;

    /// <summary>Concluded and Archived are both endings; only a captured idea still moves.</summary>
    public bool IsTerminal => this == Concluded || this == Archived;

    public static implicit operator string(IdeaState? value) => value?.Value ?? string.Empty;

    /// <summary>
    /// "Promoted" and "Discarded" are read as the two states they were reconciled into rather
    /// than wrapped as their own values: a document an old build's <c>IdeaPromoted</c>/
    /// <c>IdeaDiscarded</c> handler last wrote carries the literal old string forever (the
    /// projection is Inline, so nothing re-writes it without a new event on that idea's stream —
    /// and a terminal idea gets no more of those), and reading it under the vocabulary it always
    /// meant is the reconciliation itself, not a guess: a promoted idea concluded discovery with
    /// an outcome, and a discarded one archived it having chosen not to pursue it.
    /// </summary>
    public static implicit operator IdeaState(string? value) => value switch
    {
        null or "" => Unknown,
        "Promoted" => Concluded,
        "Discarded" => Archived,
        _ => new IdeaState(value),
    };

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
