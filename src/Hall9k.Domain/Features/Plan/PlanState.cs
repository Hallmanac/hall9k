using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// Where a plan as a whole stands. A parked plan is still Live: parking is a fact about its drafts
/// (<see cref="PlanAggregate.IsParked"/>), and what a person does about it is resume or cancel (or, at discovery's gate,
/// pursue or stop).
/// </summary>
[JsonConverter(typeof(PlanStateJsonConverter))]
public sealed record PlanState
{
    /// <summary>Being driven by the daemon, or parked for a person.</summary>
    public static readonly PlanState Live = new("Live");

    /// <summary>Every draft that was not abandoned is published. The plan's end.</summary>
    public static readonly PlanState Published = new("Published");

    /// <summary>A person cancelled it, or every draft was abandoned. The plan's other end.</summary>
    public static readonly PlanState Cancelled = new("Cancelled");

    /// <summary>A person answered discovery's gate by not pursuing the idea. A normal ending, neither a failure nor a cancellation.</summary>
    public static readonly PlanState Stopped = new("Stopped");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly PlanState Unknown = new(string.Empty);

    public string Value { get; }

    private PlanState(string value) => Value = value;

    public bool IsLive => this == Live;

    public static implicit operator string(PlanState? value) => value?.Value ?? string.Empty;

    public static implicit operator PlanState(string? value) => value switch
    {
        "Live" => Live,
        "Published" => Published,
        "Cancelled" => Cancelled,
        "Stopped" => Stopped,
        _ => Unknown,
    };

    public bool Equals(PlanState? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PlanStateJsonConverter : ClosedVocabularyJsonConverter<PlanState>
    {
        protected override PlanState FromStored(string? value) => value;

        protected override string ToStored(PlanState value) => value.Value;
    }
}
