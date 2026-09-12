using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// What this install actually did about one GitHub comment mentioning its own login (idea
/// 2f079bcd) — the outcome attached to every <see cref="ObservedReviewMention"/>, the same
/// closed-vocabulary shape <see cref="ReviewRequestOutcome"/> already uses for the review-request
/// trigger, kept as a separate type because a mention's own outcomes are not the request's: there
/// is no "already covered" here (a comment id is only ever ATTACHED to a live task or minted a
/// fresh one, never rediscovered the way a standing request is), and there is an outcome
/// (<see cref="Attached"/>) the request side has no counterpart for at all.
/// </summary>
[JsonConverter(typeof(ReviewMentionOutcomeJsonConverter))]
public sealed record ReviewMentionOutcome
{
    /// <summary>An outcome this build does not recognise — a row written by a newer one.</summary>
    public static readonly ReviewMentionOutcome Unknown = new("Unknown");

    /// <summary>No live task covered this pull request (or its only task was Done): a fresh pr-review task was minted, published and assigned for this mention.</summary>
    public static readonly ReviewMentionOutcome TaskCreated = new("TaskCreated");

    /// <summary>A live pr-review task already covered this pull request: the mention was recorded on its own stream instead of minting a second task.</summary>
    public static readonly ReviewMentionOutcome Attached = new("Attached");

    /// <summary>Nothing was minted: this project explicitly recorded <c>--auto-pr-review off</c>.</summary>
    public static readonly ReviewMentionOutcome HeldSettingOff = new("HeldSettingOff");

    /// <summary>Nothing was minted: the comment predates this project's own cutoff (the no-backfill guard).</summary>
    public static readonly ReviewMentionOutcome HeldBeforeCutoff = new("HeldBeforeCutoff");

    /// <summary>The mint was attempted and refused — a pull request that could not be imported, a race with GitHub itself.</summary>
    public static readonly ReviewMentionOutcome MintFailed = new("MintFailed");

    public string Value { get; }

    private ReviewMentionOutcome(string value) => Value = value;

    public static implicit operator string(ReviewMentionOutcome? outcome) => outcome?.Value ?? Unknown.Value;

    public static implicit operator ReviewMentionOutcome(string? value) =>
        value.IsBlank() ? Unknown : new ReviewMentionOutcome(value);

    /// <summary>Lenient mapping for a value already on a stored row; unrecognised reads as <see cref="Unknown"/>.</summary>
    public static ReviewMentionOutcome FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "taskcreated" => TaskCreated,
        "attached" => Attached,
        "heldsettingoff" => HeldSettingOff,
        "heldbeforecutoff" => HeldBeforeCutoff,
        "mintfailed" => MintFailed,
        _ => Unknown,
    };

    public bool Equals(ReviewMentionOutcome? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewMentionOutcomeJsonConverter : JsonConverter<ReviewMentionOutcome>
    {
        public override ReviewMentionOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewMentionOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
