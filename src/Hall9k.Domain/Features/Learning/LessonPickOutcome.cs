using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// How a lesson pick ended, from a closed list. Everything but <see cref="Picked"/> means the build
/// dispatched on newest-first, and the outcome is the recorded reason it did.
/// </summary>
[JsonConverter(typeof(LessonPickOutcomeJsonConverter))]
public sealed record LessonPickOutcome
{
    /// <summary>The session answered with ids and at least one stood after validation and the caps.</summary>
    public static readonly LessonPickOutcome Picked = new("picked");

    /// <summary>The executor could not start the picker session, so none ran.</summary>
    public static readonly LessonPickOutcome StartFailed = new("start-failed");

    /// <summary>The session ended in error.</summary>
    public static readonly LessonPickOutcome SessionError = new("session-error");

    /// <summary>The session ended with no result, or a blank one.</summary>
    public static readonly LessonPickOutcome NoResult = new("no-result");

    /// <summary>The session ran past its bound and was terminated.</summary>
    public static readonly LessonPickOutcome TimedOut = new("timed-out");

    /// <summary>The answer held something other than lesson ids.</summary>
    public static readonly LessonPickOutcome UnusableAnswer = new("unusable-answer");

    /// <summary>The answer was ids only, but none of them stood once validated against the offered lessons and the caps.</summary>
    public static readonly LessonPickOutcome EmptyPick = new("empty-pick");

    public static readonly LessonPickOutcome Unknown = new("");

    /// <summary>The whole vocabulary, in the order a reader meets the outcomes above.</summary>
    public static readonly IReadOnlyList<LessonPickOutcome> All =
        [Picked, StartFailed, SessionError, NoResult, TimedOut, UnusableAnswer, EmptyPick];

    public string Value { get; }

    private LessonPickOutcome(string value) => Value = value;

    /// <summary>Whether a Claude Code process was started for this pick: every outcome but <see cref="StartFailed"/>.</summary>
    public bool SpawnedSession => this != StartFailed && this != Unknown;

    public static LessonPickOutcome FromValue(string? value) =>
        All.FirstOrDefault(outcome => outcome.Value == value) ?? Unknown;

    public static implicit operator string(LessonPickOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator LessonPickOutcome(string? value) => FromValue(value);

    public bool Equals(LessonPickOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class LessonPickOutcomeJsonConverter : JsonConverter<LessonPickOutcome>
    {
        public override LessonPickOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, LessonPickOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
