using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>What kind of feedback a needs-you item holds: a whole review, or one review thread.</summary>
[JsonConverter(typeof(NonMemberFeedbackKindJsonConverter))]
public sealed record NonMemberFeedbackKind
{
    /// <summary>A CHANGES_REQUESTED review; its key is the review's url.</summary>
    public static readonly NonMemberFeedbackKind Review = new("Review");

    /// <summary>An unresolved review thread a non-member opened; its key is the thread's node id.</summary>
    public static readonly NonMemberFeedbackKind Thread = new("Thread");

    public static readonly NonMemberFeedbackKind Unknown = new("");

    public string Value { get; }

    private NonMemberFeedbackKind(string value) => Value = value;

    /// <summary>The noun a status row and a command's output use for it.</summary>
    public string Noun => this == Review ? "changes-requested review" : this == Thread ? "review thread" : "feedback";

    public static implicit operator string(NonMemberFeedbackKind? value) => value?.Value ?? string.Empty;

    public static implicit operator NonMemberFeedbackKind(string? value) => value switch
    {
        "Review" => Review,
        "Thread" => Thread,
        _ => Unknown,
    };

    public bool Equals(NonMemberFeedbackKind? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class NonMemberFeedbackKindJsonConverter : JsonConverter<NonMemberFeedbackKind>
    {
        public override NonMemberFeedbackKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, NonMemberFeedbackKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

/// <summary>Where a non-member's feedback stands with the owner.</summary>
[JsonConverter(typeof(NonMemberFeedbackStatusJsonConverter))]
public sealed record NonMemberFeedbackStatus
{
    /// <summary>The needs-you item is open: nothing dispatches, and a pre-approved merge is held.</summary>
    public static readonly NonMemberFeedbackStatus Undecided = new("Undecided");

    /// <summary>The owner gave the go. The feedback is read like a member's from here on.</summary>
    public static readonly NonMemberFeedbackStatus Admitted = new("Admitted");

    /// <summary>The owner declined. Nothing dispatches for it, and a review no longer holds the merge.</summary>
    public static readonly NonMemberFeedbackStatus Declined = new("Declined");

    /// <summary>The feedback is gone from GitHub (review dismissed, thread resolved).</summary>
    public static readonly NonMemberFeedbackStatus Withdrawn = new("Withdrawn");

    public static readonly NonMemberFeedbackStatus Unknown = new("");

    public string Value { get; }

    private NonMemberFeedbackStatus(string value) => Value = value;

    /// <summary>Whether the item still asks something of the owner.</summary>
    public bool NeedsOwner => this == Undecided;

    public static implicit operator string(NonMemberFeedbackStatus? value) => value?.Value ?? string.Empty;

    public static implicit operator NonMemberFeedbackStatus(string? value) => value switch
    {
        "Undecided" => Undecided,
        "Admitted" => Admitted,
        "Declined" => Declined,
        "Withdrawn" => Withdrawn,
        _ => Unknown,
    };

    public bool Equals(NonMemberFeedbackStatus? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class NonMemberFeedbackStatusJsonConverter : JsonConverter<NonMemberFeedbackStatus>
    {
        public override NonMemberFeedbackStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, NonMemberFeedbackStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// One piece of feedback from someone outside the project's trust-chain roster, held for the
/// owner's decision (task: a changes-requested review or an unresolved review thread from someone
/// outside the roster never dispatches a build lap; it raises a needs-you item). There is exactly
/// one per <see cref="Key"/>, however many polls observe the feedback.
/// <para>
/// It carries who, what kind, and where, and never the feedback's text: the item is what a status
/// row renders, and a stranger's words must not reach the owner's terminal unfenced any more than a
/// session's prompt. The one exception is <see cref="Review"/>, the whole review as observed, kept
/// only so the owner's go can still dispatch it after the head has moved on; it is read by the
/// prompt builder alone and never rendered by a status row.
/// </para>
/// </summary>
/// <param name="Key">The review's url, or the thread's node id.</param>
/// <param name="Reviewer">The login that wrote it; <c>ghost</c> for a deleted account.</param>
/// <param name="Url">The link the owner opens to read it themselves.</param>
/// <param name="Review">For a review: the review as observed. Null for a thread.</param>
/// <param name="Dispatched">For a review: whether a lap already carried it. A go dispatches one lap, not one per poll.</param>
/// <param name="AssessmentUrl">
/// The link to an evaluator's assessment of the feedback, shown beside the item so the owner can
/// read it before deciding. Always null today: the seam a later read-only evaluator fills.
/// </param>
public sealed record NonMemberFeedbackItem(
    string Key,
    NonMemberFeedbackKind Kind,
    string Reviewer,
    string Url,
    DateTimeOffset ObservedAt,
    NonMemberFeedbackStatus Status,
    ChangesRequestedReview? Review = null,
    bool Dispatched = false,
    string? AssessmentUrl = null);
