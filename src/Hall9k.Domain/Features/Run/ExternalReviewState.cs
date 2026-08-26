using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What the closeout monitor has observed about a pull request's external review, so a
/// Delivered task's phase can tell "waiting on Copilot" from "waiting on a human" rather than
/// sitting silent between PR-open and the merge (origin incident: PR #50 sat Delivered for 23
/// minutes with a Copilot review that had already landed at 14:02, unread before the 14:21
/// merge).
/// <para>
/// This never drives a <see cref="RunState"/> transition and is never a new task lifecycle
/// status — it is read only by the Delivered phase line. A landed review that produced
/// unresolved threads is still recorded here, ordinarily alongside a
/// <c>ReviewFeedbackReceived</c> in the same sweep, and it is that event's own
/// <see cref="RunState.ReviewPending"/> transition — not this one — that changes what the phase
/// shows next.
/// </para>
/// </summary>
[JsonConverter(typeof(ExternalReviewStateJsonConverter))]
public sealed record ExternalReviewState
{
    /// <summary>Copilot submitted a review (approval, comment, or changes-requested) that was not an error placeholder.</summary>
    public static readonly ExternalReviewState Landed = new("Landed");

    /// <summary>Copilot currently has a pending review request that has not been answered yet.</summary>
    public static readonly ExternalReviewState RequestedPending = new("RequestedPending");

    /// <summary>Neither a landed nor a requested Copilot review is on the pull request right now.</summary>
    public static readonly ExternalReviewState None = new("None");

    /// <summary>Not recognized, or a run recorded before this observation existed. Serializes as an empty string.</summary>
    public static readonly ExternalReviewState Unknown = new("");

    public string Value { get; }

    private ExternalReviewState(string value) => Value = value;

    public static implicit operator string(ExternalReviewState? state) => state?.Value ?? string.Empty;

    public static implicit operator ExternalReviewState(string? value) =>
        value.IsBlank() ? Unknown : new ExternalReviewState(value);

    public bool Equals(ExternalReviewState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ExternalReviewStateJsonConverter : JsonConverter<ExternalReviewState>
    {
        public override ExternalReviewState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ExternalReviewState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
