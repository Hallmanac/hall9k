using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What a plan's refinement session concluded, from the one outcome its report names: it revised the draft (and wrote
/// the revision beside the report), left it unchanged, or ended with a question only a person can settle.
/// <see cref="Unknown"/> is no readable outcome, which a failed session records.
/// </summary>
[JsonConverter(typeof(RefinementOutcomeJsonConverter))]
public sealed record RefinementOutcome
{
    public static readonly RefinementOutcome Revised = new("Revised", "revised");
    public static readonly RefinementOutcome Unchanged = new("Unchanged", "unchanged");
    public static readonly RefinementOutcome Question = new("Question", "question");

    /// <summary>No readable outcome. Serializes as an empty string.</summary>
    public static readonly RefinementOutcome Unknown = new(string.Empty, "no outcome");

    public string Value { get; }

    /// <summary>The word the report's closing line carries and a human reads.</summary>
    public string Word { get; }

    private RefinementOutcome(string value, string word)
    {
        Value = value;
        Word = word;
    }

    public bool IsOutcome => Value.Length > 0;

    public static implicit operator string(RefinementOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator RefinementOutcome(string? value) => value switch
    {
        "Revised" => Revised,
        "Unchanged" => Unchanged,
        "Question" => Question,
        _ => Unknown,
    };

    /// <summary>Reads the word of a report's closing line, ignoring case; anything else is not an outcome.</summary>
    public static bool TryParseWord(string? word, out RefinementOutcome outcome)
    {
        outcome = word?.Trim().ToLowerInvariant() switch
        {
            "revised" => Revised,
            "unchanged" => Unchanged,
            "question" => Question,
            _ => Unknown,
        };

        return outcome.IsOutcome;
    }

    public bool Equals(RefinementOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RefinementOutcomeJsonConverter : ClosedVocabularyJsonConverter<RefinementOutcome>
    {
        protected override RefinementOutcome FromStored(string? value) => value;

        protected override string ToStored(RefinementOutcome value) => value.Value;
    }
}
