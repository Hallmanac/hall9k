using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What an independent challenge of a draft concluded (<c>h9k task challenge</c>): publish it as written,
/// publish it once revised, or do not publish it. The closed vocabulary of the report's verdict line, and
/// what the publish gate reads. <see cref="Unknown"/> is no verdict: a failed challenge records it.
/// </summary>
[JsonConverter(typeof(ChallengeVerdictJsonConverter))]
public sealed record ChallengeVerdict
{
    public static readonly ChallengeVerdict PublishAsWritten = new("PublishAsWritten", "publish as written");
    public static readonly ChallengeVerdict PublishAsRevised = new("PublishAsRevised", "publish as revised");
    public static readonly ChallengeVerdict DoNotPublish = new("DoNotPublish", "do not publish");

    /// <summary>No verdict: a challenge that failed, or a stream written before verdicts existed. Serializes as an empty string.</summary>
    public static readonly ChallengeVerdict Unknown = new("", "no verdict");

    public string Value { get; }

    /// <summary>The words a report's verdict line carries and a human reads, lower case.</summary>
    public string Phrase { get; }

    private ChallengeVerdict(string value, string phrase)
    {
        Value = value;
        Phrase = phrase;
    }

    public static implicit operator string(ChallengeVerdict? value) => value?.Value ?? string.Empty;

    public static implicit operator ChallengeVerdict(string? value) =>
        TryParse(value, out ChallengeVerdict? parsed)
            ? parsed
            : Unknown;

    /// <summary>Whether this is one of the three verdicts rather than <see cref="Unknown"/>.</summary>
    public bool IsVerdict => Value.Length > 0;

    /// <summary>
    /// Reads either the stored value or the words of a report's verdict line, ignoring case and treating
    /// spaces, hyphens, and underscores alike. Anything else is not a verdict.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ChallengeVerdict? parsed)
    {
        string squashed = new([.. (value ?? string.Empty).Where(char.IsLetter).Select(char.ToLowerInvariant)]);
        parsed = squashed switch
        {
            "publishaswritten" => PublishAsWritten,
            "publishasrevised" => PublishAsRevised,
            "donotpublish" => DoNotPublish,
            _ => null,
        };

        return parsed is not null;
    }

    public bool Equals(ChallengeVerdict? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ChallengeVerdictJsonConverter : JsonConverter<ChallengeVerdict>
    {
        public override ChallengeVerdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ChallengeVerdict value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
