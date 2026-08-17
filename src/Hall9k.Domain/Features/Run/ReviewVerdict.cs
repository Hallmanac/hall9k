using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The independent pre-PR review agent's judgment on a run's diff (Decisions Log #23).
/// MergeReady lets PullRequestOpener proceed; NeedsFixes dispatches a fix run. Unknown
/// records a reviewer that returned no parseable verdict — the run parks rather than
/// guessing at what the reviewer meant.
/// </summary>
[JsonConverter(typeof(ReviewVerdictJsonConverter))]
public sealed record ReviewVerdict
{
    public static readonly ReviewVerdict MergeReady = new("MergeReady");
    public static readonly ReviewVerdict NeedsFixes = new("NeedsFixes");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly ReviewVerdict Unknown = new("");

    public string Value { get; }

    private ReviewVerdict(string value) => Value = value;

    public static implicit operator string(ReviewVerdict? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewVerdict(string? value) => value.IsBlank() ? Unknown : new ReviewVerdict(value);

    public bool Equals(ReviewVerdict? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewVerdictJsonConverter : JsonConverter<ReviewVerdict>
    {
        public override ReviewVerdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewVerdict value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
