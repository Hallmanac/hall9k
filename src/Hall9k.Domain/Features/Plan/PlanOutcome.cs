using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// The latest thing that happened to one draft of a plan, as the plan records it: how a refinement session ended,
/// what a challenge concluded, or that the challenge's changes were applied. Closed, so the plan holds no text.
/// <see cref="None"/> is a draft nothing has happened to yet.
/// </summary>
[JsonConverter(typeof(PlanOutcomeJsonConverter))]
public sealed record PlanOutcome
{
    public static readonly PlanOutcome Revised = new("Revised", "refinement revised the draft");
    public static readonly PlanOutcome Unchanged = new("Unchanged", "refinement left the draft unchanged");
    public static readonly PlanOutcome Question = new("Question", "refinement ended with a question");
    public static readonly PlanOutcome Applied = new("Applied", "the challenge's changes were applied");
    public static readonly PlanOutcome PublishAsWritten = new("PublishAsWritten", "challenge verdict: publish as written");
    public static readonly PlanOutcome PublishAsRevised = new("PublishAsRevised", "challenge verdict: publish as revised");
    public static readonly PlanOutcome DoNotPublish = new("DoNotPublish", "challenge verdict: do not publish");
    public static readonly PlanOutcome ChallengeFailed = new("ChallengeFailed", "the challenge ended with no verdict");
    public static readonly PlanOutcome SessionFailed = new("SessionFailed", "the refinement session failed");

    /// <summary>Nothing has happened to the draft yet. Serializes as an empty string.</summary>
    public static readonly PlanOutcome None = new(string.Empty, "nothing yet");

    public string Value { get; }

    /// <summary>What <c>h9k plan show</c> prints.</summary>
    public string Description { get; }

    private PlanOutcome(string value, string description)
    {
        Value = value;
        Description = description;
    }

    /// <summary>Whether this outcome came from a challenge's report rather than a refinement session's.</summary>
    public bool IsChallenge => this == PublishAsWritten || this == PublishAsRevised || this == DoNotPublish || this == ChallengeFailed;

    public static implicit operator string(PlanOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator PlanOutcome(string? value) => value switch
    {
        "Revised" => Revised,
        "Unchanged" => Unchanged,
        "Question" => Question,
        "Applied" => Applied,
        "PublishAsWritten" => PublishAsWritten,
        "PublishAsRevised" => PublishAsRevised,
        "DoNotPublish" => DoNotPublish,
        "ChallengeFailed" => ChallengeFailed,
        "SessionFailed" => SessionFailed,
        _ => None,
    };

    /// <summary>The outcome a challenge verdict becomes in the plan; a challenge with no verdict is a failed one.</summary>
    public static PlanOutcome FromVerdict(Tasks.ChallengeVerdict verdict) => verdict.Value switch
    {
        "PublishAsWritten" => PublishAsWritten,
        "PublishAsRevised" => PublishAsRevised,
        "DoNotPublish" => DoNotPublish,
        _ => ChallengeFailed,
    };

    public bool Equals(PlanOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PlanOutcomeJsonConverter : ClosedVocabularyJsonConverter<PlanOutcome>
    {
        protected override PlanOutcome FromStored(string? value) => value;

        protected override string ToStored(PlanOutcome value) => value.Value;
    }
}
