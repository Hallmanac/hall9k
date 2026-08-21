using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How much an adversarial review finding matters (Decisions Log #62). The three grades are
/// what the severity gate reads: from the gate cycle onward only a High forces another
/// adversarial cycle, while Mediums and Lows are still fixed and simply stop re-triggering
/// the loop. The anchors are stated to the reviewer in the prompt rather than left to its
/// intuition, because a grade every reviewer invents for itself is not a gate.
/// <para>
/// The conformance track grades nothing: its findings are all "the work does not match what
/// it promised", which has no useful severity ordering, so a conformance finding is recorded
/// <see cref="Unknown"/> by design rather than assigned a grade nobody asked for.
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
    /// Not graded: a reviewer that stated no severity, a conformance finding (that track has no
    /// grades), or a needs-fixes verdict whose findings could not be read as structured blocks.
    /// Serializes as an empty string.
    /// <para>
    /// Ungraded is treated as gate-forcing wherever the gate is consulted, and as not-routable
    /// wherever the scope tag is. That is the conservative reading rather than a guess at what
    /// the reviewer meant: a finding nobody graded has not been shown to be safe to wave
    /// through, and the alternative — quietly scoring it Low — is exactly the self-downgrade
    /// the gate exists to prevent.
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
