using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Why a refinement session was dispatched: to refine a draft before its first challenge, to apply the changes of a
/// <c>publish as revised</c> verdict, or to answer a <c>do not publish</c> verdict before the next round. The prompt
/// differs for each, and so does what the plan does with the session's outcome.
/// </summary>
[JsonConverter(typeof(RefinementPurposeJsonConverter))]
public sealed record RefinementPurpose
{
    public static readonly RefinementPurpose Refine = new("Refine", "refine the draft");
    public static readonly RefinementPurpose ApplyVerdict = new("ApplyVerdict", "apply the verdict's changes");
    public static readonly RefinementPurpose AnswerVerdict = new("AnswerVerdict", "answer the verdict");

    /// <summary>Not set. Serializes as an empty string.</summary>
    public static readonly RefinementPurpose Unknown = new(string.Empty, "no purpose");

    public string Value { get; }

    public string Description { get; }

    private RefinementPurpose(string value, string description)
    {
        Value = value;
        Description = description;
    }

    public static implicit operator string(RefinementPurpose? value) => value?.Value ?? string.Empty;

    public static implicit operator RefinementPurpose(string? value) => value switch
    {
        "Refine" => Refine,
        "ApplyVerdict" => ApplyVerdict,
        "AnswerVerdict" => AnswerVerdict,
        _ => Unknown,
    };

    public bool Equals(RefinementPurpose? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RefinementPurposeJsonConverter : ClosedVocabularyJsonConverter<RefinementPurpose>
    {
        protected override RefinementPurpose FromStored(string? value) => value;

        protected override string ToStored(RefinementPurpose value) => value.Value;
    }
}
