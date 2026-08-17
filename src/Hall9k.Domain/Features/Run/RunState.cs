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
    public static readonly RunState AwaitingReview = new("AwaitingReview");
    /// <summary>Closeout: the PR's CI checks completed and failed; a fix follow-up (or a park) is on the way.</summary>
    public static readonly RunState ChecksFailing = new("ChecksFailing");
    /// <summary>Closeout: unresolved Copilot review threads observed; a resolve follow-up (or a park) is on the way.</summary>
    public static readonly RunState ReviewPending = new("ReviewPending");
    /// <summary>Closeout: automatic retries exhausted; the human owns the PR, the monitor still watches for the merge.</summary>
    public static readonly RunState CloseoutParked = new("CloseoutParked");
    public static readonly RunState Completed = new("Completed");
    public static readonly RunState Failed = new("Failed");
    public static readonly RunState Killed = new("Killed");
    public static readonly RunState Superseded = new("Superseded");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly RunState Unknown = new("");

    public string Value { get; }

    private RunState(string value) => Value = value;

    public bool IsTerminal => this == Completed || this == Failed || this == Killed || this == Superseded;

    public bool IsLive => this == Dispatched || this == Running || this == Verifying;

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
