using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What came of asking a run for its handoff summary (Decisions Log #36). A run that closes
/// out without a usable handoff is perfectly valid — a park a human resolved by hand, a
/// stream written before handoffs existed — so the absence is one of the recorded answers
/// rather than an empty string nobody can interpret (the AGENTS.md never-guess rule).
/// </summary>
[JsonConverter(typeof(HandoffOutcomeJsonConverter))]
public sealed record HandoffOutcome
{
    /// <summary>The agent authored a handoff and the platform read it off the session's own result.</summary>
    public static readonly HandoffOutcome Captured = new("Captured");

    /// <summary>The session's result was read and carried no handoff block. Observed, not assumed.</summary>
    public static readonly HandoffOutcome NotAuthored = new("NotAuthored");

    /// <summary>
    /// There was no session-end capture to read at all: a run parked and resolved by hand, an
    /// agent that never reported a result, or a run from before the capture existed.
    /// </summary>
    public static readonly HandoffOutcome NotCaptured = new("NotCaptured");

    /// <summary>
    /// There is no run to ask yet: nothing carried the task to true closeout, so nothing has
    /// been handed down. A blocker still in flight reads this way, as does one a human
    /// attested Done without a merge (log #27). Not recorded on a run — only a query about a
    /// task can observe it.
    /// </summary>
    public static readonly HandoffOutcome NotClosedOut = new("NotClosedOut");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly HandoffOutcome Unknown = new("");

    public string Value { get; }

    private HandoffOutcome(string value) => Value = value;

    /// <summary>Whether a dependent has real handoff text to read, rather than a recorded absence.</summary>
    public bool HasSummary => this == Captured;

    /// <summary>Why there is no handoff, in the words a human (or a downstream prompt) reads.</summary>
    public string Describe() =>
        this == Captured
            ? "handed off by the run's own agent at session end"
            : this == NotAuthored
                ? "the run's session ended without a handoff block"
                : this == NotCaptured
                    ? "no session-end output was captured for the run that closed this out"
                    : this == NotClosedOut
                        ? "no run has carried it to true closeout, so there is nothing to hand down yet"
                        : "not recorded";

    public static implicit operator string(HandoffOutcome? outcome) => outcome?.Value ?? string.Empty;

    public static implicit operator HandoffOutcome(string? value) => value.IsBlank() ? Unknown : new HandoffOutcome(value);

    public bool Equals(HandoffOutcome? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class HandoffOutcomeJsonConverter : JsonConverter<HandoffOutcome>
    {
        public override HandoffOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, HandoffOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
