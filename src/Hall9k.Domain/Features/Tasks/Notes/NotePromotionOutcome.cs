using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// How the closeout promotion step ended, from a closed list. Everything but <see cref="Kept"/> and
/// <see cref="NothingKept"/> means the step failed: nothing was promoted and each offered note was
/// dropped as step failed. The step is never retried.
/// </summary>
[JsonConverter(typeof(NotePromotionOutcomeJsonConverter))]
public sealed record NotePromotionOutcome
{
    /// <summary>The session answered and one note became a lesson.</summary>
    public static readonly NotePromotionOutcome Kept = new("kept");

    /// <summary>The session answered with a usable answer and no note stood, so nothing was promoted.</summary>
    public static readonly NotePromotionOutcome NothingKept = new("nothing-kept");

    /// <summary>The executor could not start the session, so none ran.</summary>
    public static readonly NotePromotionOutcome StartFailed = new("start-failed");

    /// <summary>The session ended in error.</summary>
    public static readonly NotePromotionOutcome SessionError = new("session-error");

    /// <summary>The session ended with no result, or a blank one.</summary>
    public static readonly NotePromotionOutcome NoResult = new("no-result");

    /// <summary>The session ran past its bound and was terminated.</summary>
    public static readonly NotePromotionOutcome TimedOut = new("timed-out");

    /// <summary>The answer was neither NONE nor lines of the keep form.</summary>
    public static readonly NotePromotionOutcome UnusableAnswer = new("unusable-answer");

    /// <summary>The session chose a note and the write of the lesson and the record failed.</summary>
    public static readonly NotePromotionOutcome LessonWriteFailed = new("lesson-write-failed");

    public static readonly NotePromotionOutcome Unknown = new("");

    /// <summary>The whole vocabulary, in the order a reader meets the outcomes above.</summary>
    public static readonly IReadOnlyList<NotePromotionOutcome> All =
        [Kept, NothingKept, StartFailed, SessionError, NoResult, TimedOut, UnusableAnswer, LessonWriteFailed];

    public string Value { get; }

    private NotePromotionOutcome(string value) => Value = value;

    /// <summary>Whether the session reached a decision: it answered and the answer was usable.</summary>
    public bool Decided => this == Kept || this == NothingKept;

    /// <summary>Whether a Claude Code process was started for this step: every outcome but <see cref="StartFailed"/>.</summary>
    public bool SpawnedSession => this != StartFailed && this != Unknown;

    public static NotePromotionOutcome FromValue(string? value) =>
        All.FirstOrDefault(outcome => outcome.Value == value) ?? Unknown;

    public static implicit operator string(NotePromotionOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator NotePromotionOutcome(string? value) => FromValue(value);

    public bool Equals(NotePromotionOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class NotePromotionOutcomeJsonConverter : JsonConverter<NotePromotionOutcome>
    {
        public override NotePromotionOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, NotePromotionOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
