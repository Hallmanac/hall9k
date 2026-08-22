using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How a review track — and, once every track has ended, the run's whole review — reached
/// merge-ready (Decisions Log #63). The terminal verdict stays
/// <see cref="ReviewVerdict.MergeReady"/> either way; this is the honesty that rides beside
/// it, because "a reviewer saw this exact tip and found nothing" and "the severity gate ended
/// the loop over findings that were fixed but never re-read" are not the same claim and a
/// single verdict word cannot tell them apart.
/// </summary>
[JsonConverter(typeof(ReviewSettlementJsonConverter))]
public sealed record ReviewSettlement
{
    /// <summary>A reviewer read the final tip and found nothing. No residuals.</summary>
    public static readonly ReviewSettlement Clean = new("Clean");

    /// <summary>
    /// The loop ended without a clean read of the final tip: the severity gate closed a track
    /// over Mediums and Lows, findings were routed to draft bug tasks, or a human resolved a
    /// park. Whatever residuals that left are recorded alongside it.
    /// </summary>
    public static readonly ReviewSettlement Settled = new("Settled");

    /// <summary>Not recognized, or a review that has not ended yet. Serializes as an empty string.</summary>
    public static readonly ReviewSettlement Unknown = new("");

    public string Value { get; }

    private ReviewSettlement(string value) => Value = value;

    public static implicit operator string(ReviewSettlement? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewSettlement(string? value) =>
        value.IsBlank() ? Unknown : new ReviewSettlement(value);

    public bool Equals(ReviewSettlement? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewSettlementJsonConverter : JsonConverter<ReviewSettlement>
    {
        public override ReviewSettlement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewSettlement value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
