using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>The execution lifecycle of one dispatch attempt (TASK-MODEL.md §2).</summary>
[JsonConverter(typeof(RunStateJsonConverter))]
public sealed record RunState
{
    public static readonly RunState Dispatched = new("Dispatched");
    public static readonly RunState Running = new("Running");
    public static readonly RunState Verifying = new("Verifying");
    /// <summary>Pre-PR review loop (log #24): an independent review or fix session owns the run between the gates and the PR.</summary>
    public static readonly RunState UnderReview = new("UnderReview");
    /// <summary>Pre-PR review loop: automatic fixes exhausted or a finding disputed; the human owns the next move. Surfaces as NeedsHuman.</summary>
    public static readonly RunState ReviewParked = new("ReviewParked");
    public static readonly RunState AwaitingReview = new("AwaitingReview");
    /// <summary>Closeout: the PR's CI checks completed and failed; a fix follow-up (or a park) is on the way.</summary>
    public static readonly RunState ChecksFailing = new("ChecksFailing");
    /// <summary>
    /// Closeout: review attention observed — unresolved Copilot review threads (a resolve
    /// follow-up or a park is on the way) or an errored Copilot review (the run holds here
    /// while the monitor's re-requested review is answered).
    /// </summary>
    public static readonly RunState ReviewPending = new("ReviewPending");
    /// <summary>Closeout: automatic retries exhausted; the human owns the PR, the monitor still watches for the merge.</summary>
    public static readonly RunState CloseoutParked = new("CloseoutParked");
    /// <summary>
    /// The session's result carried the usage-limit message shape (Decisions Log #40): the
    /// subscription window ran dry, an external and clock-recoverable cause, not a machine or
    /// code fault. The task stays Claimed and the hourly retry sweep clears this without a
    /// human act — distinct from ReviewParked, which waits on a person.
    /// </summary>
    public static readonly RunState BudgetParked = new("BudgetParked");
    public static readonly RunState Completed = new("Completed");
    public static readonly RunState Failed = new("Failed");
    public static readonly RunState Killed = new("Killed");
    public static readonly RunState Superseded = new("Superseded");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly RunState Unknown = new("");

    public string Value { get; }

    private RunState(string value) => Value = value;

    public bool IsTerminal => this == Completed || this == Failed || this == Killed || this == Superseded;

    public bool IsLive => this == Dispatched || this == Running || this == Verifying || this == UnderReview;

    public static implicit operator string(RunState? value) => value?.Value ?? string.Empty;

    public static implicit operator RunState(string? value) => value.IsBlank() ? Unknown : new RunState(value);

    public bool Equals(RunState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunStateJsonConverter : JsonConverter<RunState>
    {
        public override RunState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RunState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
