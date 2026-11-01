using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Why a draft challenge ended with no verdict: a closed list, so the event holds nothing a session or a
/// stranger wrote. <see cref="Unknown"/> is "no failure", which is what a challenge that did produce a
/// verdict records.
/// </summary>
[JsonConverter(typeof(ChallengeFailureReasonJsonConverter))]
public sealed record ChallengeFailureReason
{
    public static readonly ChallengeFailureReason StartFailed = new(
        "StartFailed", "the session could not be started");

    public static readonly ChallengeFailureReason SessionError = new(
        "SessionError", "the session ended in error");

    public static readonly ChallengeFailureReason NoResult = new(
        "NoResult", "the session ended without a result");

    public static readonly ChallengeFailureReason TimedOut = new(
        "TimedOut", "the session exceeded its time bound and was terminated");

    public static readonly ChallengeFailureReason NoReport = new(
        "NoReport", "the session wrote no report");

    public static readonly ChallengeFailureReason UnreadableReport = new(
        "UnreadableReport", "the report's closing block was not a readable verdict");

    public static readonly ChallengeFailureReason NoLongerDraft = new(
        "NoLongerDraft", "the task was no longer a draft when the daemon came to dispatch it, so no session ran");

    public static readonly ChallengeFailureReason DaemonRestarted = new(
        "DaemonRestarted", "the daemon stopped or restarted while the session ran");

    public static readonly ChallengeFailureReason DispatchExpired = new(
        "DispatchExpired", "another node dispatched it and recorded no outcome past the foreign-session ceiling");

    /// <summary>No failure. Serializes as an empty string.</summary>
    public static readonly ChallengeFailureReason Unknown = new("", "no failure");

    public string Value { get; }

    /// <summary>The sentence fragment <c>h9k task show</c> and the orchestrator feed print for this reason.</summary>
    public string Description { get; }

    private ChallengeFailureReason(string value, string description)
    {
        Value = value;
        Description = description;
    }

    public static implicit operator string(ChallengeFailureReason? value) => value?.Value ?? string.Empty;

    /// <summary>Maps a stored value back onto its instance, so Description survives a round trip; an unrecognized value reads as <see cref="Unknown"/>.</summary>
    public static implicit operator ChallengeFailureReason(string? value) => value switch
    {
        "StartFailed" => StartFailed,
        "SessionError" => SessionError,
        "NoResult" => NoResult,
        "TimedOut" => TimedOut,
        "NoReport" => NoReport,
        "UnreadableReport" => UnreadableReport,
        "NoLongerDraft" => NoLongerDraft,
        "DaemonRestarted" => DaemonRestarted,
        "DispatchExpired" => DispatchExpired,
        _ => Unknown,
    };

    public bool Equals(ChallengeFailureReason? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ChallengeFailureReasonJsonConverter : JsonConverter<ChallengeFailureReason>
    {
        public override ChallengeFailureReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ChallengeFailureReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
