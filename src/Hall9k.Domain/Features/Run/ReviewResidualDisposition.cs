using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What became of a finding that a track ended on (Decisions Log #62). It is a separate
/// vocabulary from <see cref="ReviewFindingDisposition"/> on purpose: that one says what the
/// loop decided to do with a finding while the loop was still running, and this one says what
/// the record shows once the loop is over — including the part a verdict alone would hide,
/// that the fix was never read by a reviewer.
/// </summary>
[JsonConverter(typeof(ReviewResidualDispositionJsonConverter))]
public sealed record ReviewResidualDisposition
{
    /// <summary>
    /// Handed to the terminal fix session and shipped without another review pass. That is the
    /// deliberate trade the severity gate makes; recording it is what keeps the history honest
    /// about having made it. What the fix session actually did is its own record (its
    /// RESOLUTION line and the branch's diff), never assumed here.
    /// </summary>
    public static readonly ReviewResidualDisposition FixedUnreviewed = new("FixedUnreviewed");

    /// <summary>Routed to a draft bug task rather than fixed in this pull request.</summary>
    public static readonly ReviewResidualDisposition Routed = new("Routed");

    /// <summary>
    /// Meant for a draft bug task, and no draft exists: composing or storing it failed, and the
    /// routing event carries the reason. It is its own disposition rather than a shade of
    /// <see cref="Routed"/> because the two are opposite facts for whoever picks the branch up
    /// — one says the defect is written down somewhere a human will see it, the other says it
    /// survives only in this run's stream — and counting them together would report a draft
    /// that was never created.
    /// </summary>
    public static readonly ReviewResidualDisposition RoutingFailed = new("RoutingFailed");

    /// <summary>Not recognized. Serializes as an empty string.</summary>
    public static readonly ReviewResidualDisposition Unknown = new("");

    public string Value { get; }

    private ReviewResidualDisposition(string value) => Value = value;

    public static implicit operator string(ReviewResidualDisposition? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewResidualDisposition(string? value) =>
        value.IsBlank() ? Unknown : new ReviewResidualDisposition(value);

    public bool Equals(ReviewResidualDisposition? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewResidualDispositionJsonConverter : JsonConverter<ReviewResidualDisposition>
    {
        public override ReviewResidualDisposition Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(
            Utf8JsonWriter writer, ReviewResidualDisposition value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
