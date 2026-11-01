using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// Where one draft of a plan stands: being refined, being challenged, or published. Discovery and drafting are not
/// phases a plan offers yet. A draft that is ready to publish stays in the phase it last finished; readiness is its
/// own fact (<see cref="PlanDraftState.Ready"/>).
/// </summary>
[JsonConverter(typeof(PlanPhaseJsonConverter))]
public sealed record PlanPhase
{
    public static readonly PlanPhase Refinement = new("Refinement", "refinement");
    public static readonly PlanPhase Challenge = new("Challenge", "challenge");
    public static readonly PlanPhase Published = new("Published", "published");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly PlanPhase Unknown = new(string.Empty, "unknown");

    public string Value { get; }

    /// <summary>The word <c>h9k plan show</c> and the options of <c>h9k plan start</c> use.</summary>
    public string Word { get; }

    private PlanPhase(string value, string word)
    {
        Value = value;
        Word = word;
    }

    public static implicit operator string(PlanPhase? value) => value?.Value ?? string.Empty;

    public static implicit operator PlanPhase(string? value) => value switch
    {
        "Refinement" => Refinement,
        "Challenge" => Challenge,
        "Published" => Published,
        _ => Unknown,
    };

    /// <summary>Reads the word an operator types (<c>refinement</c> or <c>challenge</c>), or null when it is neither.</summary>
    public static PlanPhase? ParseStart(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "refinement" => Refinement,
        "challenge" => Challenge,
        _ => null,
    };

    public bool Equals(PlanPhase? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PlanPhaseJsonConverter : ClosedVocabularyJsonConverter<PlanPhase>
    {
        protected override PlanPhase FromStored(string? value) => value;

        protected override string ToStored(PlanPhase value) => value.Value;
    }
}
