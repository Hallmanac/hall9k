using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// Where a lesson stands (idea d805fd8b, piece 1). Retirement is the only ending this piece
/// ships, and it is an explicit act carrying a reason: no lesson is ever retired on age or on
/// absence of reinforcement, because a lesson that works suppresses its own evidence
/// (IDEA-learning-capture, "Staleness").
/// <para>
/// Distillation shipped without adding the third value, Superseded, that this doc used to
/// promise (idea d805fd8b, piece 5). A merged-away lesson retires like any other, with a reason
/// naming the lesson that absorbed it, which is one of the three endings retirement was already
/// documented to carry, and the survivor cites it back
/// (<see cref="LearningRecorded.DistilledFrom"/>), so the merge is recorded from both ends
/// already. A second terminal value would have split "live" across two clauses in the renderer,
/// the prompt feed, and <c>h9k learn list</c> to record nothing the reason string does not. It
/// stays a static instance away if that trade ever turns out wrong.
/// </para>
/// </summary>
[JsonConverter(typeof(LearningStatusJsonConverter))]
public sealed record LearningStatus
{
    /// <summary>Recorded and live: no gate, no approval step (IDEA-learning-capture, "Why there is no quarantine").</summary>
    public static readonly LearningStatus Active = new("Active");

    /// <summary>No longer true, or no longer needed, with the reason recorded. Terminal, and never a deletion.</summary>
    public static readonly LearningStatus Retired = new("Retired");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly LearningStatus Unknown = new("");

    public string Value { get; }

    private LearningStatus(string value) => Value = value;

    public static implicit operator string(LearningStatus? status) => status?.Value ?? string.Empty;

    public static implicit operator LearningStatus(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : new LearningStatus(value);

    public bool Equals(LearningStatus? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class LearningStatusJsonConverter : JsonConverter<LearningStatus>
    {
        public override LearningStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, LearningStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
