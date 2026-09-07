using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// A human reviewer's own verdict at the end of their review lap (<c>h9k pr approve</c> /
/// <c>h9k pr request-changes</c>) — deliberately NOT
/// <see cref="Hall9k.Domain.Features.Run.ReviewVerdict"/>, which is the pre-PR review agent's
/// judgment on a diff this platform may fix and merge. These two never mean the same thing: this
/// one is the GitHub review event the reviewer submitted on somebody else's pull request, and it
/// travels through that pull request rather than through any Hall9k pipeline. Sharing one type
/// would let a MergeReady be read as an APPROVE, which is precisely the conflation the pr-review
/// task type exists to avoid.
/// <para>
/// <see cref="Unknown"/> is the sentinel every closed vocabulary in this codebase carries
/// (TASK-MODEL.md §8): a stream written before this feature existed replays as Unknown rather
/// than as a verdict nobody ever gave.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewerVerdictJsonConverter))]
public sealed record ReviewerVerdict
{
    /// <summary>The reviewer approved: <c>APPROVE</c> on GitHub.</summary>
    public static readonly ReviewerVerdict Approved = new("Approved");

    /// <summary>The reviewer asked for changes: <c>REQUEST_CHANGES</c> on GitHub.</summary>
    public static readonly ReviewerVerdict ChangesRequested = new("ChangesRequested");

    /// <summary>No verdict recorded. Serializes as an empty string.</summary>
    public static readonly ReviewerVerdict Unknown = new("");

    public string Value { get; }

    private ReviewerVerdict(string value) => Value = value;

    /// <summary>
    /// The GitHub review event this verdict submits as. Empty for <see cref="Unknown"/>, which no
    /// caller ever posts — the poster refuses it rather than defaulting to a review event GitHub
    /// would accept.
    /// </summary>
    public string GitHubEvent => this == Approved
        ? "APPROVE"
        : this == ChangesRequested ? "REQUEST_CHANGES" : string.Empty;

    public static implicit operator string(ReviewerVerdict? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewerVerdict(string? value) => value.IsBlank() ? Unknown : new ReviewerVerdict(value);

    public bool Equals(ReviewerVerdict? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewerVerdictJsonConverter : JsonConverter<ReviewerVerdict>
    {
        public override ReviewerVerdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewerVerdict value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
