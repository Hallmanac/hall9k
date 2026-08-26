using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How much a review finding matters (Decisions Log #63). The three grades are what the
/// adversarial severity gate reads: from the gate cycle onward only a High forces another
/// adversarial cycle, while a Medium is still fixed that cycle without forcing another, and a
/// Low stops being fixed on its own entirely — see <see cref="MeetsFixBar"/>. The anchors are
/// stated to the reviewer in the prompt rather than left to its intuition, because a grade every
/// reviewer invents for itself is not a gate.
/// <para>
/// Both lenses grade every finding (Decisions Log #87): conformance's own convergence rule
/// (Decisions Log #63) still runs on a plain clean-or-not basis, with no multi-cycle severity
/// gate of its own, but its findings now carry a real grade the same way adversarial's do,
/// because the fix-bar this type also defines (<see cref="MeetsFixBar"/>) reads every lens's
/// output the same way.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewSeverityJsonConverter))]
public sealed record ReviewSeverity
{
    /// <summary>A correctness, security, or data-integrity defect reachable in realistic use.</summary>
    public static readonly ReviewSeverity High = new("High");

    /// <summary>A real defect with bounded or unlikely impact, or a doctrine violation that misleads without corrupting.</summary>
    public static readonly ReviewSeverity Medium = new("Medium");

    /// <summary>Polish.</summary>
    public static readonly ReviewSeverity Low = new("Low");

    /// <summary>
    /// Not graded: a reviewer (either lens, since Decisions Log #87) that stated no severity, or
    /// a needs-fixes verdict whose findings could not be read as structured blocks. Serializes as
    /// an empty string.
    /// <para>
    /// Ungraded is treated as gate-forcing wherever the adversarial severity gate is consulted,
    /// and as not-routable wherever the scope tag is. That is the conservative reading rather
    /// than a guess at what the reviewer meant: a finding nobody graded has not been shown to be
    /// safe to wave through, and the alternative — quietly scoring it Low — is exactly the
    /// self-downgrade the gate exists to prevent. <see cref="MeetsFixBar"/> deliberately reads
    /// Unknown the other way around — see its own doc for why.
    /// </para>
    /// </summary>
    public static readonly ReviewSeverity Unknown = new("");

    public string Value { get; }

    private ReviewSeverity(string value) => Value = value;

    /// <summary>
    /// Whether the reviewer stated a grade below High. This is the one predicate the gate is
    /// built on, and it is deliberately phrased as what the reviewer <i>stated</i>: everything
    /// else — a High, an ungraded finding, a word the parser did not recognize — takes the
    /// conservative branch by the reasoning on <see cref="Unknown"/>.
    /// </summary>
    public bool IsStatedBelowHigh => this == Medium || this == Low;

    /// <summary>Whether this severity forces another adversarial cycle once the gate applies.</summary>
    public bool ForcesAnotherCycle => !IsStatedBelowHigh;

    /// <summary>
    /// Whether a finding graded this way earns a fix session of its own <i>this cycle</i>
    /// (Decisions Log #87) — the bar behind "a review pass records needs-fixes only when at
    /// least one finding is graded medium or higher". This is a different question from
    /// <see cref="IsStatedBelowHigh"/>/<see cref="ForcesAnotherCycle"/>, which govern the
    /// adversarial track's own multi-cycle severity gate and are untouched by this bar: a Medium
    /// still never forces another adversarial cycle past the gate, and a High still always does,
    /// exactly as before. This bar sits in front of that one, deciding whether a finding is fixed
    /// at all right now rather than how many more times it gets looked at once it is.
    /// <para>
    /// Deliberately does not treat <see cref="Unknown"/> the conservative way the rest of this
    /// type does: origin (2026-08-25 token telemetry) found the conformance lens issuing a
    /// needs-fixes verdict over a single trivial finding 59 times in one day, and once both
    /// lenses are told to grade every finding explicitly (Decisions Log #87 also closes
    /// conformance's own gap here), a finding that still comes back ungraded is read the same as
    /// one graded Low — the platform cannot tell a lazy omission from genuine polish, and
    /// spending a whole fix-and-re-review cycle on the chance it was the former is exactly the
    /// waste this bar exists to stop. It is not lost either way: see
    /// <see cref="ReviewFindingDisposition.RideAlong"/>.
    /// </para>
    /// </summary>
    public bool MeetsFixBar => this == High || this == Medium;

    /// <summary>
    /// Reads a reviewer's own word for the grade. Anything unrecognized is
    /// <see cref="Unknown"/> — the parser never picks a grade the reviewer did not write.
    /// </summary>
    public static ReviewSeverity Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => High,
        "medium" or "med" => Medium,
        "low" => Low,
        _ => Unknown,
    };

    public static implicit operator string(ReviewSeverity? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewSeverity(string? value) => value.IsBlank() ? Unknown : new ReviewSeverity(value);

    public bool Equals(ReviewSeverity? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewSeverityJsonConverter : JsonConverter<ReviewSeverity>
    {
        public override ReviewSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewSeverity value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
