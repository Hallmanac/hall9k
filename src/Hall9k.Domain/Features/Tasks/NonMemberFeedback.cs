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

    /// <summary>
    /// The feedback is gone from GitHub (review dismissed, thread resolved), or a thread's approved text
    /// was edited, which ends the approval; the next poll raises the thread again as a fresh item.
    /// </summary>
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
/// It carries who, what kind, and where, and the feedback's text only where the owner's go needs it
/// pinned: <see cref="Review"/>, the whole review as observed, and <see cref="CommentCopies"/>, the
/// opener's comments as observed. The item is what a status row renders, and a stranger's words must
/// not reach the owner's terminal unfenced any more than a session's prompt, so those two are read by
/// the gate and the prompt builder alone and never rendered by a status row.
/// </para>
/// </summary>
/// <param name="Key">The review's url, or the thread's node id.</param>
/// <param name="Reviewer">The login that wrote it; <c>ghost</c> for a deleted account.</param>
/// <param name="Url">The link the owner opens to read it themselves.</param>
/// <param name="Review">For a review: the review as observed. Null for a thread.</param>
/// <param name="Dispatched">For a review: whether a lap already carried it. A go dispatches one lap, not one per poll.</param>
/// <param name="CommentCopies">
/// For a thread: the opener's comments as they read when it was held, which is all the owner's go
/// covers. A comment added afterwards, even by the same account, was never shown to the owner, and one
/// whose text has changed since is no longer the text approved. Null for a review.
/// </param>
/// <param name="Evaluation">
/// Where the read-only evaluator's assessment of this feedback stands: not started, running, assessed
/// (with its recommendation and the file to read), failed, or never dispatched because the task is past
/// its evaluator cap. Advisory only: the owner's go and decline work the same in every state.
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
    FeedbackEvaluation? Evaluation = null,
    IReadOnlyList<ThreadCommentCopy>? CommentCopies = null)
{
    /// <summary>The evaluator's state for this item, never null.</summary>
    [JsonIgnore]
    public FeedbackEvaluation EvaluationState => Evaluation ?? FeedbackEvaluation.None;

    /// <summary>
    /// The item once an evaluator is dispatched for it. An item that already has an evaluation (one
    /// dispatched, or skipped past the cap) keeps it, so a replayed or doubled event cannot start a second.
    /// </summary>
    public NonMemberFeedbackItem WithEvaluatorDispatched(Guid sessionId, string assessmentPath) =>
        EvaluationState.Status == FeedbackEvaluationStatus.Unknown
            ? this with { Evaluation = new FeedbackEvaluation(FeedbackEvaluationStatus.Running, sessionId, assessmentPath) }
            : this;

    /// <summary>
    /// The item once an evaluation outcome is recorded. A session's outcome lands only on the item that
    /// session was dispatched for and only while it is running, so an outcome that arrives for an
    /// earlier instance of a re-raised item, or twice, changes nothing; a skip lands only on an item no
    /// evaluator has been considered for.
    /// </summary>
    public NonMemberFeedbackItem WithEvaluationRecorded(
        FeedbackEvaluationStatus outcome, Guid? sessionId, FeedbackRecommendation? recommendation, string? reason)
    {
        FeedbackEvaluation current = EvaluationState;
        bool lands = outcome == FeedbackEvaluationStatus.PastCap
            ? current.Status == FeedbackEvaluationStatus.Unknown
            : current.Status == FeedbackEvaluationStatus.Running && current.SessionId == sessionId;
        return lands && outcome != FeedbackEvaluationStatus.Running && outcome != FeedbackEvaluationStatus.Unknown
            ? this with
            {
                Evaluation = current with
                {
                    Status = outcome,
                    Recommendation = outcome == FeedbackEvaluationStatus.Assessed ? recommendation : null,
                    Reason = outcome == FeedbackEvaluationStatus.Assessed ? null : reason,
                },
            }
            : this;
    }
}

/// <summary>Where an evaluator's assessment of one piece of feedback stands.</summary>
[JsonConverter(typeof(FeedbackEvaluationStatusJsonConverter))]
public sealed record FeedbackEvaluationStatus
{
    /// <summary>No evaluator has been considered for the item yet; the next closeout poll decides.</summary>
    public static readonly FeedbackEvaluationStatus Unknown = new("");

    /// <summary>An evaluator was dispatched and has not recorded an outcome.</summary>
    public static readonly FeedbackEvaluationStatus Running = new("Running");

    /// <summary>The evaluator wrote an assessment with a recommendation of fix, decline or ask.</summary>
    public static readonly FeedbackEvaluationStatus Assessed = new("Assessed");

    /// <summary>The evaluator failed, timed out, wrote nothing, or wrote a recommendation outside fix, decline and ask.</summary>
    public static readonly FeedbackEvaluationStatus Failed = new("Failed");

    /// <summary>The task had already dispatched its lifetime allowance of evaluators, so none ran for this item.</summary>
    public static readonly FeedbackEvaluationStatus PastCap = new("PastCap");

    public string Value { get; }

    private FeedbackEvaluationStatus(string value) => Value = value;

    public static implicit operator string(FeedbackEvaluationStatus? value) => value?.Value ?? string.Empty;

    public static implicit operator FeedbackEvaluationStatus(string? value) => value switch
    {
        "Running" => Running,
        "Assessed" => Assessed,
        "Failed" => Failed,
        "PastCap" => PastCap,
        _ => Unknown,
    };

    public bool Equals(FeedbackEvaluationStatus? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FeedbackEvaluationStatusJsonConverter : JsonConverter<FeedbackEvaluationStatus>
    {
        public override FeedbackEvaluationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, FeedbackEvaluationStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// What an evaluator recommends the owner do with a piece of feedback. The only word of an assessment
/// that ever reaches <c>h9k status</c> or the orchestrator feed, so it is a closed vocabulary parsed
/// from the file's own first line and never free text.
/// </summary>
[JsonConverter(typeof(FeedbackRecommendationJsonConverter))]
public sealed record FeedbackRecommendation
{
    /// <summary>The requested change serves the task's intent; the owner's go would dispatch a fix lap.</summary>
    public static readonly FeedbackRecommendation Fix = new("fix");

    /// <summary>The requested change misses the task's intent; the owner would decline it.</summary>
    public static readonly FeedbackRecommendation Decline = new("decline");

    /// <summary>The feedback cannot be judged without the owner or the reviewer saying more.</summary>
    public static readonly FeedbackRecommendation Ask = new("ask");

    /// <summary>No recommendation, or one outside the three words above.</summary>
    public static readonly FeedbackRecommendation Unknown = new("");

    public string Value { get; }

    private FeedbackRecommendation(string value) => Value = value;

    /// <summary>Whether this is one of the three words an assessment may recommend.</summary>
    public bool IsRecognized => this != Unknown;

    /// <summary>The recommendation a word names, matched exactly (ignoring case and surrounding space), or <see cref="Unknown"/>.</summary>
    public static FeedbackRecommendation Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "fix" => Fix,
        "decline" => Decline,
        "ask" => Ask,
        _ => Unknown,
    };

    public static implicit operator string(FeedbackRecommendation? value) => value?.Value ?? string.Empty;

    public static implicit operator FeedbackRecommendation(string? value) => Parse(value);

    public bool Equals(FeedbackRecommendation? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FeedbackRecommendationJsonConverter : JsonConverter<FeedbackRecommendation>
    {
        public override FeedbackRecommendation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, FeedbackRecommendation value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// One item's evaluator state. It carries a recommendation word, the path of the file the owner reads
/// the assessment in, and a daemon-composed reason when no assessment exists; it never carries a word
/// of the assessment itself, because the orchestrator window reads what renders this with permissions
/// bypassed.
/// </summary>
/// <param name="Status">Where the evaluation stands.</param>
/// <param name="SessionId">The evaluator session dispatched for this item, which ties its outcome to the item instance it was dispatched for.</param>
/// <param name="AssessmentPath">The file the evaluator was asked to write, set once it is dispatched.</param>
/// <param name="Recommendation">The recommendation word, once assessed.</param>
/// <param name="Reason">Why there is no assessment, in the daemon's own words, for a failed or skipped evaluation.</param>
public sealed record FeedbackEvaluation(
    FeedbackEvaluationStatus Status,
    Guid? SessionId = null,
    string? AssessmentPath = null,
    FeedbackRecommendation? Recommendation = null,
    string? Reason = null)
{
    public static readonly FeedbackEvaluation None = new(FeedbackEvaluationStatus.Unknown);
}
