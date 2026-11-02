using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Feedback;

/// <summary>
/// How one feedback step ended, a closed list so the record holds nothing a session wrote. The step is the check the daemon
/// runs over the notes a task's blockers left, once, at the moment the task would otherwise be released
/// (<see cref="TaskAggregate.FeedbackStepHeld"/>). Three outcomes release the task to Queued as it would have gone without the
/// step, one revises it and queues it again, one parks it for a person, and one records that a person got there first.
/// </summary>
[JsonConverter(typeof(FeedbackStepOutcomeJsonConverter))]
public sealed record FeedbackStepOutcome
{
    public static readonly FeedbackStepOutcome ReleasedWithoutCheck = new(
        "ReleasedWithoutCheck", "released without a check", "no blocker left a note the trust rule lets into a prompt", true);

    public static readonly FeedbackStepOutcome ReleasedAfterCheck = new(
        "ReleasedAfterCheck", "released after the check", "the check found the blockers' notes do not affect the task", true);

    public static readonly FeedbackStepOutcome ReleasedAfterFailedCheck = new(
        "ReleasedAfterFailedCheck", "released after a failed check", "the check failed, timed out, or gave no readable answer, so the task was released rather than held up", true);

    public static readonly FeedbackStepOutcome RevisedAndRequeued = new(
        "RevisedAndRequeued", "revised and queued again", "the notes affected the task, it was refined, challenged once, published, and queued again for the same owner and node", false);

    public static readonly FeedbackStepOutcome Parked = new(
        "Parked", "parked for a person", "the step could not finish on its own", false);

    public static readonly FeedbackStepOutcome EndedWithoutActing = new(
        "EndedWithoutActing", "ended without acting", "a person changed the task while the step ran, so the step stood down", false);

    /// <summary>Not recognized or no step ran. Serializes as an empty string.</summary>
    public static readonly FeedbackStepOutcome Unknown = new(string.Empty, "no feedback step", string.Empty, false);

    public string Value { get; }

    /// <summary>What <c>h9k task show</c> and <c>h9k status</c> print.</summary>
    public string Description { get; }

    /// <summary>The sentence behind it, for the surfaces that explain.</summary>
    public string Meaning { get; }

    /// <summary>Whether this outcome moves a held task from Blocked to Queued: the three that released it.</summary>
    public bool ReleasesTask { get; }

    private FeedbackStepOutcome(string value, string description, string meaning, bool releasesTask)
    {
        Value = value;
        Description = description;
        Meaning = meaning;
        ReleasesTask = releasesTask;
    }

    public bool IsKnown => Value.Length > 0;

    public static implicit operator string(FeedbackStepOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator FeedbackStepOutcome(string? value) => value switch
    {
        "ReleasedWithoutCheck" => ReleasedWithoutCheck,
        "ReleasedAfterCheck" => ReleasedAfterCheck,
        "ReleasedAfterFailedCheck" => ReleasedAfterFailedCheck,
        "RevisedAndRequeued" => RevisedAndRequeued,
        "Parked" => Parked,
        "EndedWithoutActing" => EndedWithoutActing,
        _ => Unknown,
    };

    public bool Equals(FeedbackStepOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FeedbackStepOutcomeJsonConverter : ClosedVocabularyJsonConverter<FeedbackStepOutcome>
    {
        protected override FeedbackStepOutcome FromStored(string? value) => value;

        protected override string ToStored(FeedbackStepOutcome value) => value.Value;
    }
}
