using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// What this install actually did about one GitHub comment mentioning its own login (idea
/// 2f079bcd) — the outcome attached to every <see cref="ObservedReviewMention"/>, the same
/// closed-vocabulary shape <see cref="ReviewRequestOutcome"/> already uses for the review-request
/// trigger, kept as a separate type because a mention's own outcomes are not the request's: there
/// is no "already covered" here (a comment id is only ever ATTACHED to this owner's live task,
/// minted a fresh one, or recorded against no task when only another owner's covers the pull
/// request, never rediscovered the way a standing request is), and there are outcomes
/// (<see cref="Attached"/>, <see cref="CoveredByTeammate"/>, <see cref="AuthorReplied"/>) the request side has no counterpart
/// for at all.
/// </summary>
[JsonConverter(typeof(ReviewMentionOutcomeJsonConverter))]
public sealed record ReviewMentionOutcome
{
    /// <summary>An outcome this build does not recognise — a row written by a newer one.</summary>
    public static readonly ReviewMentionOutcome Unknown = new("Unknown");

    /// <summary>No live task covered this pull request (or its only task was Done): a fresh pr-review task was minted, published and assigned for this mention.</summary>
    public static readonly ReviewMentionOutcome TaskCreated = new("TaskCreated");

    /// <summary>
    /// A fresh pr-review task was minted and published for this mention, but deliberately never
    /// assigned — the membership gate found the pull request's own author, the mentioning comment's
    /// own author, or both, was not a declared hall9k team member (or was a Bot) on a repository the
    /// gate covers (security review idea 6be68ee2, finding 1; independent pre-PR review, cycle 3,
    /// conformance lens, added the pull request's own author to a check that used to read the
    /// comment's alone). <c>h9k task assign</c> is the human go.
    /// </summary>
    public static readonly ReviewMentionOutcome TaskCreatedParked = new("TaskCreatedParked");

    /// <summary>
    /// The pull request was authored by this install's own login and no live task of this owner's
    /// covered it, so a pr-review task was minted, published and assigned to answer this one comment
    /// with the bounded mention answer lap, never the two-lens review of the owner's own work
    /// (decision dce39370).
    /// </summary>
    public static readonly ReviewMentionOutcome AnswerOnlyTaskCreated = new("AnswerOnlyTaskCreated");

    /// <summary>
    /// <see cref="AnswerOnlyTaskCreated"/>, held by the membership gate exactly as
    /// <see cref="TaskCreatedParked"/> is: published but never assigned, with <c>h9k task assign</c>
    /// the human go. Once assigned it still runs the answer lap and never the two-lens review.
    /// </summary>
    public static readonly ReviewMentionOutcome AnswerOnlyTaskCreatedParked = new("AnswerOnlyTaskCreatedParked");

    /// <summary>A live pr-review task already covered this pull request, and a bounded follow-up lap was dispatched to answer this exact comment.</summary>
    public static readonly ReviewMentionOutcome Attached = new("Attached");

    /// <summary>
    /// A live pr-review task already covered this pull request, but no follow-up lap was
    /// dispatched to answer this exact comment — the task was not currently eligible for one, the
    /// comment predates this project's own cutoff, the setting is off here, or this sweep's own
    /// launch ceiling or a node-wide hold stood in the way. The mention is recorded on the task's
    /// own stream so it is not lost from the record, but nothing ever re-decides it (the identical
    /// one-shot dedupe every other outcome gets), so this is the one shape that must stay visible
    /// somewhere or the tagged comment is silently lost with no run ever answering it (independent
    /// pre-PR review, cycle 1, both lenses).
    /// </summary>
    public static readonly ReviewMentionOutcome AttachedNoFollowUp = new("AttachedNoFollowUp");

    /// <summary>
    /// The only live task covering this pull request (matched by its external reference, whatever
    /// its type) belongs to another owner (the rule the CLI task commands apply,
    /// <c>TaskOwnerRule</c>, with a task this node cannot attribute never counting as this
    /// owner's), so nothing was claimed, launched, appended to that task's
    /// stream or minted. The mention is recorded against no task and surfaced as a needs-you row:
    /// the pull request is a teammate's review, and the reply happens on GitHub.
    /// </summary>
    public static readonly ReviewMentionOutcome CoveredByTeammate = new("CoveredByTeammate");

    /// <summary>
    /// The pull request's own author answered this owner's review: the comment's author is the pull
    /// request's author, and this owner's own pr-review task is following the pull request through
    /// (<c>TaskDecider.AwaitsPrReviewFollowThrough</c>). The mention is recorded on the task's own
    /// stream and shown as a row with the reply's opening lines and a link, and nothing is claimed
    /// or launched for it: reading one comment does not need a run that analyzes the whole pull
    /// request. It is not an <see cref="Attached"/> outcome, so it never counts toward the mention
    /// follow-up lifetime cap or its cooldown.
    /// </summary>
    public static readonly ReviewMentionOutcome AuthorReplied = new("AuthorReplied");

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
        "taskcreatedparked" => TaskCreatedParked,
        "answeronlytaskcreated" => AnswerOnlyTaskCreated,
        "answeronlytaskcreatedparked" => AnswerOnlyTaskCreatedParked,
        "attached" => Attached,
        "attachednofollowup" => AttachedNoFollowUp,
        "coveredbyteammate" => CoveredByTeammate,
        "authorreplied" => AuthorReplied,
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
