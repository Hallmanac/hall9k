using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What a spike's own review cycle decided against its stated exit criterion, or why it never got
/// a decision at all: Met (the reviewer judged the findings and the branch satisfy the exit
/// criterion), NotMet (they still do not, after the one fix lap a spike gets), or BudgetExhausted
/// (the build session's own turn, token, or wall-clock budget was crossed before the exit
/// criterion was ever judged — recorded with whatever findings were written, and never Failed:
/// PLAN.md §16 PLACEHOLDER-1d81543a's ruling that a budget-ended spike closes out normally).
/// </summary>
[JsonConverter(typeof(SpikeVerdictJsonConverter))]
public sealed record SpikeVerdict
{
    public static readonly SpikeVerdict Met = new("Met");
    public static readonly SpikeVerdict NotMet = new("NotMet");
    public static readonly SpikeVerdict BudgetExhausted = new("BudgetExhausted");
    /// <summary>No verdict recorded yet — every spike's default until its review cycle concludes.</summary>
    public static readonly SpikeVerdict Unknown = new("");

    public string Value { get; }

    private SpikeVerdict(string value) => Value = value;

    public static implicit operator string(SpikeVerdict? value) => value?.Value ?? string.Empty;

    public static implicit operator SpikeVerdict(string? value) => value.IsBlank() ? Unknown : new SpikeVerdict(value);

    public static bool TryParse(string? value, [NotNullWhen(true)] out SpikeVerdict? parsed)
    {
        parsed = value?.Trim().ToLowerInvariant() switch
        {
            "met" => Met,
            "notmet" or "not-met" or "not_met" => NotMet,
            "budgetexhausted" or "budget-exhausted" or "budget_exhausted" => BudgetExhausted,
            _ => null,
        };

        return parsed is not null;
    }

    public bool Equals(SpikeVerdict? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class SpikeVerdictJsonConverter : JsonConverter<SpikeVerdict>
    {
        public override SpikeVerdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, SpikeVerdict value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
