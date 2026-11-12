using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What a plan's refinement session concluded, from the one outcome its report names: it revised the draft (and wrote
/// the revision beside the report), left it unchanged, ended with a question only a person can settle, or proposed a split.
/// <see cref="Unknown"/> is no readable outcome, which a failed session records.
/// </summary>
[JsonConverter(typeof(RefinementOutcomeJsonConverter))]
public sealed record RefinementOutcome
{
    public static readonly RefinementOutcome Revised = new("Revised", "revised");
    public static readonly RefinementOutcome Unchanged = new("Unchanged", "unchanged");
    public static readonly RefinementOutcome Question = new("Question", "question");

    /// <summary>A feedback step's refinement concluded the task should not run at all: the notes its blockers left remove its reason to exist. Only that session's prompt offers it.</summary>
    public static readonly RefinementOutcome ShouldNotRun = new("ShouldNotRun", "should-not-run");

    /// <summary>
    /// The session proposed splitting the draft into increments, named in order on the lines after its closing line
    /// (<see cref="Plan.RefinementSplitReport"/>). Read only from that whole block, never from the word alone, so
    /// <see cref="TryParseWord"/> does not know it. Only an ordinary plan's prompt offers it.
    /// </summary>
    public static readonly RefinementOutcome Split = new("Split", "split");

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
        "ShouldNotRun" => ShouldNotRun,
        "Split" => Split,
        _ => Unknown,
    };

    /// <summary>
    /// Reads the word of a report's closing line, ignoring case; anything else is not an outcome. A split is not read here: a
    /// split line is an outcome only with the increment lines after it.
    /// </summary>
    public static bool TryParseWord(string? word, out RefinementOutcome outcome)
    {
        outcome = word?.Trim().ToLowerInvariant() switch
        {
            "revised" => Revised,
            "unchanged" => Unchanged,
            "question" => Question,
            "should-not-run" or "should not run" => ShouldNotRun,
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
