using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Feedback;

/// <summary>
/// What the feedback check answered: the blockers' notes affect the task, or they do not. <see cref="Failed"/> is no readable
/// answer, which a session that failed, timed out, or ended without the closing line records; the step treats it as
/// not affecting, so a failed check never holds a task up.
/// </summary>
[JsonConverter(typeof(FeedbackCheckAnswerJsonConverter))]
public sealed record FeedbackCheckAnswer
{
    public static readonly FeedbackCheckAnswer Affects = new("Affects", "affects");
    public static readonly FeedbackCheckAnswer Unaffected = new("Unaffected", "unaffected");

    /// <summary>No readable answer. Serializes as an empty string.</summary>
    public static readonly FeedbackCheckAnswer Failed = new(string.Empty, "no answer");

    public string Value { get; }

    /// <summary>The word the closing line carries and a person reads.</summary>
    public string Word { get; }

    private FeedbackCheckAnswer(string value, string word)
    {
        Value = value;
        Word = word;
    }

    public bool IsAnswer => Value.Length > 0;

    public static implicit operator string(FeedbackCheckAnswer? value) => value?.Value ?? string.Empty;

    public static implicit operator FeedbackCheckAnswer(string? value) => value switch
    {
        "Affects" => Affects,
        "Unaffected" => Unaffected,
        _ => Failed,
    };

    /// <summary>Reads the word of a closing line, ignoring case; anything else is no answer.</summary>
    public static bool TryParseWord(string? word, out FeedbackCheckAnswer answer)
    {
        answer = word?.Trim().ToLowerInvariant() switch
        {
            "affects" => Affects,
            "unaffected" => Unaffected,
            _ => Failed,
        };

        return answer.IsAnswer;
    }

    public bool Equals(FeedbackCheckAnswer? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FeedbackCheckAnswerJsonConverter : ClosedVocabularyJsonConverter<FeedbackCheckAnswer>
    {
        protected override FeedbackCheckAnswer FromStored(string? value) => value;

        protected override string ToStored(FeedbackCheckAnswer value) => value.Value;
    }
}
