using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What the review loop decided to do with one finding (Decisions Log #63), recorded by the
/// machinery rather than by any agent: severity and scope are the reviewer's observations,
/// and this is the platform's decision over them. Recording it per finding is what lets the
/// history answer "which findings actually cost a cycle" without re-deriving the policy that
/// was in force at the time.
/// </summary>
[JsonConverter(typeof(ReviewFindingDispositionJsonConverter))]
public sealed record ReviewFindingDisposition
{
    /// <summary>Handed to this cycle's fix session, to be resolved in this pull request.</summary>
    public static readonly ReviewFindingDisposition Fix = new("Fix");

    /// <summary>
    /// Not fixed here: an out-of-scope Medium or Low, sent to a draft bug task so a
    /// pre-existing defect neither grows this diff nor gets forgotten.
    /// </summary>
    public static readonly ReviewFindingDisposition Route = new("Route");

    /// <summary>Not recognized, or a pass recorded before dispositions existed. Serializes as an empty string.</summary>
    public static readonly ReviewFindingDisposition Unknown = new("");

    public string Value { get; }

    private ReviewFindingDisposition(string value) => Value = value;

    public static implicit operator string(ReviewFindingDisposition? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewFindingDisposition(string? value) =>
        value.IsBlank() ? Unknown : new ReviewFindingDisposition(value);

    public bool Equals(ReviewFindingDisposition? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewFindingDispositionJsonConverter : JsonConverter<ReviewFindingDisposition>
    {
        public override ReviewFindingDisposition Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(
            Utf8JsonWriter writer, ReviewFindingDisposition value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
