using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Why a piece of review feedback is allowed to reach a session that skips permissions, recorded
/// beside the feedback itself when closeout dispatches a lap (task: feedback from outside the
/// project's trust chain never reaches a build lap without the owner's go). The daemon is the only
/// place that can answer it, because the project's roster lives in its memory; everything that
/// reads the feedback afterwards (the prompt builder above all) can only check that an answer was
/// recorded.
/// <para>
/// <see cref="Unknown"/> is the absence of an answer, and it is deliberately the reading of every
/// review stored before this vocabulary existed: a review carrying no admission is treated as a
/// non-member's, so a lap queued before the gate shipped never renders its text.
/// </para>
/// </summary>
[JsonConverter(typeof(FeedbackAdmissionJsonConverter))]
public sealed record FeedbackAdmission
{
    /// <summary>The author's numeric GitHub account id is in the project's trust-chain roster.</summary>
    public static readonly FeedbackAdmission Member = new("Member");

    /// <summary>An app account, Copilot included. Bots keep the automated thread path they have always been on.</summary>
    public static readonly FeedbackAdmission Bot = new("Bot");

    /// <summary>The author is outside the roster and the owner gave the go for this feedback.</summary>
    public static readonly FeedbackAdmission OwnerApproved = new("OwnerApproved");

    /// <summary>Not recorded. Serializes as an empty string, and reads as a non-member.</summary>
    public static readonly FeedbackAdmission Unknown = new("");

    public string Value { get; }

    private FeedbackAdmission(string value) => Value = value;

    /// <summary>Whether a session may read this feedback's text: anything but <see cref="Unknown"/>.</summary>
    public bool MayBeRead => this != Unknown;

    public static implicit operator string(FeedbackAdmission? value) => value?.Value ?? string.Empty;

    public static implicit operator FeedbackAdmission(string? value) => value switch
    {
        "Member" => Member,
        "Bot" => Bot,
        "OwnerApproved" => OwnerApproved,
        _ => Unknown,
    };

    public bool Equals(FeedbackAdmission? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FeedbackAdmissionJsonConverter : JsonConverter<FeedbackAdmission>
    {
        public override FeedbackAdmission Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, FeedbackAdmission value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
