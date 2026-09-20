using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What kind of thing a review finding is, where the kind changes how it is read rather than how
/// it is routed (idea b9b09779, piece 1). Severity and scope already decide routing, and a kind
/// never overrides either: a run-skill drift finding is fixed, routed, or carried as a ride-along
/// on exactly the terms every other finding of the same grade and scope is.
/// <para>
/// The vocabulary is deliberately tiny. A kind earns its place only when something downstream has
/// to treat the finding differently in kind rather than in degree, and today exactly one does.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewFindingKindJsonConverter))]
public sealed record ReviewFindingKind
{
    /// <summary>
    /// The change altered how the application runs locally, so whatever tells a human or an agent
    /// how to run it is now out of date. The answer to the standing question every review pass
    /// carries; the finding says what changed about running it, and the fix updates the run
    /// instructions alongside the code.
    /// </summary>
    public static readonly ReviewFindingKind RunSkillDrift = new("run-skill-drift");

    /// <summary>
    /// An ordinary finding — the overwhelming majority, and what every finding recorded before
    /// kinds existed is. Serializes as the empty string, so an old run stream replays unchanged.
    /// </summary>
    public static readonly ReviewFindingKind Unknown = new("");

    public string Value { get; }

    private ReviewFindingKind(string value) => Value = value;

    /// <summary>Reads a reviewer's own word for the tag; anything unrecognized stays <see cref="Unknown"/>.</summary>
    public static ReviewFindingKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-") switch
        {
            "run-skill-drift" or "run-skill" or "runskilldrift" => RunSkillDrift,
            _ => Unknown,
        };

    public static implicit operator string(ReviewFindingKind? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewFindingKind(string? value) =>
        value.IsBlank() ? Unknown : new ReviewFindingKind(value);

    public bool Equals(ReviewFindingKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewFindingKindJsonConverter : JsonConverter<ReviewFindingKind>
    {
        public override ReviewFindingKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewFindingKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
