using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How a review-fix session resolved the findings it was handed (Decisions Log #23).
/// Fixed re-enters the loop (gates, then a fresh review). Disputed means the fix run
/// judged a finding not-a-defect or human-territory — the run parks with both positions
/// recorded rather than looping on a judgment call. Unknown records a fix session that
/// declared no resolution; the re-review that follows establishes the real state.
/// <para>
/// <see cref="WaitingOnBackgroundGate"/> is the named case of Unknown a fix session's own final
/// message names explicitly (task: a headless build, fix, or recovery session never ends its turn
/// while a gate it started is still running in the background) — a session that ends saying it is
/// still waiting on a backgrounded build or test gate, per
/// <c>Hall9k.Daemon.Review.PendingBackgroundTaskParser</c>. Recorded so <c>h9k task show</c>
/// and the run log say the session ended waiting on a background gate rather than reporting the
/// fix as undeclared (origin: three occurrences in eleven hours, 2026-09-07/08, each logged
/// "(undeclared)" until now). The loop still treats it like any other Unknown for control flow —
/// re-entering Reverify, where the leg-scoped automatic recovery this same task adds gives the
/// dirty tree it usually leaves behind a real chance to resolve on its own — this value only
/// changes what gets NAMED, never what happens next.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewFixOutcomeJsonConverter))]
public sealed record ReviewFixOutcome
{
    public static readonly ReviewFixOutcome Fixed = new("Fixed");
    public static readonly ReviewFixOutcome Disputed = new("Disputed");
    public static readonly ReviewFixOutcome WaitingOnBackgroundGate = new("WaitingOnBackgroundGate");
    /// <summary>Not recognized or not declared. Serializes as an empty string.</summary>
    public static readonly ReviewFixOutcome Unknown = new("");

    public string Value { get; }

    private ReviewFixOutcome(string value) => Value = value;

    public static implicit operator string(ReviewFixOutcome? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewFixOutcome(string? value) => value.IsBlank() ? Unknown : new ReviewFixOutcome(value);

    public bool Equals(ReviewFixOutcome? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewFixOutcomeJsonConverter : JsonConverter<ReviewFixOutcome>
    {
        public override ReviewFixOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewFixOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
