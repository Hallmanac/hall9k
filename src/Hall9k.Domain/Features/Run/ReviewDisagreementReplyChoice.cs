using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What the implementer chose to do with a parked disagreement's proposed reply
/// (<c>h9k review resolve</c>, task: a changes-requested pull-request review from a human becomes
/// a fix lap). It lands on the run stream rather than staying in process, so it is a sealed
/// record with static instances and an <see cref="Unknown"/> sentinel rather than an enum
/// (TASK-MODEL.md §8).
/// </summary>
[JsonConverter(typeof(ReviewDisagreementReplyChoiceJsonConverter))]
public sealed record ReviewDisagreementReplyChoice
{
    /// <summary>Post each parked disagreement's proposed reply exactly as the session drafted it.</summary>
    public static readonly ReviewDisagreementReplyChoice AsWritten = new("AsWritten");

    /// <summary>Post the implementer's own text instead of the drafted one.</summary>
    public static readonly ReviewDisagreementReplyChoice Edited = new("Edited");

    /// <summary>Post nothing at all — the reviewer hears nothing from the platform about this.</summary>
    public static readonly ReviewDisagreementReplyChoice Nothing = new("Nothing");

    /// <summary>Not recognized, or a resolve recorded before this vocabulary existed. Serializes as an empty string.</summary>
    public static readonly ReviewDisagreementReplyChoice Unknown = new("");

    public string Value { get; }

    private ReviewDisagreementReplyChoice(string value) => Value = value;

    public static implicit operator string(ReviewDisagreementReplyChoice? choice) => choice?.Value ?? string.Empty;

    public static implicit operator ReviewDisagreementReplyChoice(string? value) =>
        value.IsBlank() ? Unknown : new ReviewDisagreementReplyChoice(value);

    public bool Equals(ReviewDisagreementReplyChoice? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewDisagreementReplyChoiceJsonConverter : JsonConverter<ReviewDisagreementReplyChoice>
    {
        public override ReviewDisagreementReplyChoice Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(
            Utf8JsonWriter writer, ReviewDisagreementReplyChoice value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
