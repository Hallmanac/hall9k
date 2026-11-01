using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// Why a plan parked for a person: a closed list, so the record holds nothing a session or a draft wrote. The
/// prose behind a park stays in the files on the node that ran the session, and <c>h9k plan show</c> says where.
/// </summary>
[JsonConverter(typeof(PlanParkReasonJsonConverter))]
public sealed record PlanParkReason
{
    public static readonly PlanParkReason RoundsExhausted = new(
        "RoundsExhausted",
        "a challenge said do not publish on the last round the plan may run",
        "h9k plan resume grants one more round; or revise, publish, or abandon the draft by hand");

    public static readonly PlanParkReason Question = new(
        "Question",
        "the refinement session ended with a question only a person can settle",
        "answer it in the draft's journal.md or with h9k task revise, then h9k plan resume");

    public static readonly PlanParkReason VerdictNotApplied = new(
        "VerdictNotApplied",
        "the session told to apply or answer the challenge verdict changed nothing",
        "apply the verdict by hand with h9k task revise, or h9k plan resume to run the session again");

    public static readonly PlanParkReason DraftChanged = new(
        "DraftChanged",
        "the draft was changed after the refinement session was dispatched, so its revision was not applied",
        "h9k plan resume runs the session again on the draft as it is now");

    public static readonly PlanParkReason RevisionRefused = new(
        "RevisionRefused",
        "the revision the session wrote was refused",
        "read the refusal in the plan's session directory, then h9k plan resume to run the session again");

    public static readonly PlanParkReason SessionFailed = new(
        "SessionFailed",
        "the refinement session failed",
        "h9k plan resume runs it again");

    public static readonly PlanParkReason ChallengeFailed = new(
        "ChallengeFailed",
        "the challenge ended with no verdict",
        "h9k plan resume requests it again");

    public static readonly PlanParkReason PublishRefused = new(
        "PublishRefused",
        "publishing the draft was refused",
        "read the refusal in the plan's session directory and fix what it names, then h9k plan resume");

    /// <summary>Not recognized or not parked. Serializes as an empty string.</summary>
    public static readonly PlanParkReason Unknown = new(string.Empty, "not parked", string.Empty);

    public string Value { get; }

    /// <summary>The sentence fragment <c>h9k plan show</c>, <c>h9k status</c>, and the feed print for this reason.</summary>
    public string Description { get; }

    /// <summary>What a person can do about it.</summary>
    public string WayForward { get; }

    private PlanParkReason(string value, string description, string wayForward)
    {
        Value = value;
        Description = description;
        WayForward = wayForward;
    }

    public bool IsParked => Value.Length > 0;

    public static implicit operator string(PlanParkReason? value) => value?.Value ?? string.Empty;

    public static implicit operator PlanParkReason(string? value) => value switch
    {
        "RoundsExhausted" => RoundsExhausted,
        "Question" => Question,
        "VerdictNotApplied" => VerdictNotApplied,
        "DraftChanged" => DraftChanged,
        "RevisionRefused" => RevisionRefused,
        "SessionFailed" => SessionFailed,
        "ChallengeFailed" => ChallengeFailed,
        "PublishRefused" => PublishRefused,
        _ => Unknown,
    };

    public bool Equals(PlanParkReason? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PlanParkReasonJsonConverter : ClosedVocabularyJsonConverter<PlanParkReason>
    {
        protected override PlanParkReason FromStored(string? value) => value;

        protected override string ToStored(PlanParkReason value) => value.Value;
    }
}
