using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

[JsonConverter(typeof(RequeueReasonJsonConverter))]
public sealed record RequeueReason
{
    public static readonly RequeueReason LeaseExpired = new("LeaseExpired");
    public static readonly RequeueReason RunFailedRetryable = new("RunFailedRetryable");
    public static readonly RequeueReason HumanRequested = new("HumanRequested");
    /// <summary>
    /// A pull-request review pre-flight (idea 6be68ee2, finding 1, phase one) came back safe for
    /// the task's current head oid: the lease is given back so the next claim finds that recorded
    /// verdict and proceeds straight to the worktree, rather than the primary session that would
    /// otherwise have dispatched into it.
    /// </summary>
    public static readonly RequeueReason PrReviewPreflightSafe = new("PrReviewPreflightSafe");
    /// <summary>
    /// A pull-request review pre-flight's own session ended without a usable verdict — a budget
    /// exhaustion or a launch failure, the same non-fatal, redispatchable shape <c>PrReviewEngine</c>
    /// gives its own follow-on sessions — so the lease is given back to try a fresh pre-flight
    /// rather than parking the task as unsafe.
    /// </summary>
    public static readonly RequeueReason PrReviewPreflightRetry = new("PrReviewPreflightRetry");
    /// <summary>
    /// The identical safe verdict <see cref="PrReviewPreflightSafe"/> gives, for a pre-flight
    /// dispatched to gate a mention follow-up's own checkout rather than an ordinary review
    /// dispatch: the next claim answers the mentioning comment through
    /// <c>RunLauncher.LaunchPrReviewMentionFollowUpAsync</c> instead of running a fresh full
    /// review (independent pre-PR review, cycle 1, both lenses — a bare <see cref="PrReviewPreflightSafe"/>
    /// requeue here let the ordinary dispatch loop's own <c>LaunchAsync</c> claim it and answer the
    /// mentioning comment with an unrequested full review instead, never answering the comment at
    /// all).
    /// </summary>
    public static readonly RequeueReason PrReviewPreflightSafeMentionFollowUp = new("PrReviewPreflightSafeMentionFollowUp");
    /// <summary>
    /// The identical never-reached-a-verdict shape <see cref="PrReviewPreflightRetry"/> gives, for a
    /// pre-flight dispatched to gate a mention follow-up's own checkout rather than an ordinary
    /// review dispatch: the next claim retries through
    /// <c>RunLauncher.LaunchPrReviewMentionFollowUpAsync</c>, which dispatches a fresh
    /// mention-flagged pre-flight itself, instead of falling into an ordinary full-review dispatch
    /// that never answers the mentioning comment (independent pre-PR review, cycle 3, conformance
    /// lens — a bare <see cref="PrReviewPreflightRetry"/> requeue here cleared
    /// <c>PendingMentionFollowUpAfterPreflight</c> the same way an ordinary safe verdict does, so the
    /// next claim ran an unrequested full review and the mention was lost for good, since
    /// <c>ObservedReviewMention</c> dedups it permanently).
    /// </summary>
    public static readonly RequeueReason PrReviewPreflightRetryMentionFollowUp = new("PrReviewPreflightRetryMentionFollowUp");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly RequeueReason Unknown = new("");

    public string Value { get; }

    private RequeueReason(string value) => Value = value;

    public static implicit operator string(RequeueReason? value) => value?.Value ?? string.Empty;

    public static implicit operator RequeueReason(string? value) => value.IsBlank() ? Unknown : new RequeueReason(value);

    public bool Equals(RequeueReason? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RequeueReasonJsonConverter : JsonConverter<RequeueReason>
    {
        public override RequeueReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RequeueReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
