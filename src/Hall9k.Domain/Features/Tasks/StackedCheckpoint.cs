using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Which of a stacked child's own defined checkpoints a rebase onto its parent's current head was
/// taken at (task: a stacked child absorbs its parent's post-delivery churn safely). A closed
/// vocabulary that lands on an event, so a sealed record with static instances rather than an enum
/// (TASK-MODEL.md §8).
/// <para>
/// There are exactly two, and naming them is the point of the type: a stacked child does NOT chase
/// every push its parent makes while it is in flight — a Delivered parent still takes review laps
/// and closeout follow-ups, and each one moves its branch — it catches up at these points and
/// nowhere else, so the work between them is done against one stable base rather than a moving one.
/// </para>
/// </summary>
[JsonConverter(typeof(StackedCheckpointJsonConverter))]
public sealed record StackedCheckpoint
{
    /// <summary>
    /// Immediately before the child's own first review cycle dispatches. The reviewer-facing
    /// checkpoint: a review pass reads the child's delta against its recorded base, so a parent
    /// that moved since the cut would otherwise have its own already-reviewed work read — and
    /// graded in-scope — as this child's.
    /// </summary>
    public static readonly StackedCheckpoint BeforeFirstReviewCycle = new("BeforeFirstReviewCycle");

    /// <summary>
    /// Immediately before the mandatory final full pass, alongside the ordinary pre-final-pass
    /// rebase every unstacked run takes onto the project's base. The mergeable-on-arrival
    /// checkpoint: the last point at which the branch can be brought onto its parent's head before
    /// the pull request is opened or pushed.
    /// </summary>
    public static readonly StackedCheckpoint BeforeFinalPass = new("BeforeFinalPass");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly StackedCheckpoint Unknown = new("");

    public string Value { get; }

    private StackedCheckpoint(string value) => Value = value;

    /// <summary>
    /// What this checkpoint precedes, as a noun phrase a caller puts "before" in front of — a log
    /// line, a park message, an audit sentence. Deliberately without the preposition, so a caller
    /// that needs a different one ("the rebase owed before …", "skipping the rebase before …") is
    /// not left composing "before before".
    /// </summary>
    public string Describe() => this == BeforeFirstReviewCycle
        ? "this run's first review cycle"
        : this == BeforeFinalPass
            ? "the mandatory final full pass"
            : "an unrecorded checkpoint";

    public static implicit operator string(StackedCheckpoint? value) => value?.Value ?? string.Empty;

    public static implicit operator StackedCheckpoint(string? value) =>
        value.IsBlank() ? Unknown : new StackedCheckpoint(value);

    public bool Equals(StackedCheckpoint? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class StackedCheckpointJsonConverter : JsonConverter<StackedCheckpoint>
    {
        public override StackedCheckpoint Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, StackedCheckpoint value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
