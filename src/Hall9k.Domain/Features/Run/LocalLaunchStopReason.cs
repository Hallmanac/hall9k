using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Why a local launch ended (idea b9b09779, piece 5). A closed vocabulary rather than a free
/// string because the four reasons are genuinely different to a reader of the run's history: one
/// is the reviewer saying they are finished, and the other three are the platform tearing a launch
/// down on their behalf, each for a different fact it observed.
/// </summary>
[JsonConverter(typeof(LocalLaunchStopReasonJsonConverter))]
public sealed record LocalLaunchStopReason
{
    /// <summary><c>h9k task run-local &lt;task&gt; --stop</c>: the reviewer is done walking it.</summary>
    public static readonly LocalLaunchStopReason Requested = new("requested");

    /// <summary>The task this launch belongs to reached a terminal state, so nobody is reviewing it any more.</summary>
    public static readonly LocalLaunchStopReason TaskClosedOut = new("task-closed-out");

    /// <summary>The worktree the launch was standing in is gone, so whatever is still running is running against a checkout that no longer exists.</summary>
    public static readonly LocalLaunchStopReason WorktreeRemoved = new("worktree-removed");

    /// <summary>
    /// The recorded process was already gone when the platform next looked. Recorded rather than
    /// passed over so the run's history says the launch ended, and says the platform did not end
    /// it — an app that crashed and one somebody killed by hand look identical from here, and this
    /// value claims neither.
    /// </summary>
    public static readonly LocalLaunchStopReason ProcessGone = new("process-gone");

    /// <summary>Not one of the four — an unparsed or unrecognized value off an old event.</summary>
    public static readonly LocalLaunchStopReason Unknown = new(string.Empty);

    /// <summary>Every real reason, for a help line that names them all.</summary>
    public static readonly IReadOnlyList<LocalLaunchStopReason> All =
        [Requested, TaskClosedOut, WorktreeRemoved, ProcessGone];

    public string Value { get; }

    private LocalLaunchStopReason(string value) => Value = value;

    public static implicit operator string(LocalLaunchStopReason? reason) => reason?.Value ?? string.Empty;

    /// <summary>Lenient mapping for a value already on a stream; unrecognized reads as <see cref="Unknown"/>.</summary>
    public static LocalLaunchStopReason FromInput(string? value) =>
        All.FirstOrDefault(reason => reason.Value == value?.Trim().ToLowerInvariant()) ?? Unknown;

    /// <summary>How the stop reads in a sentence a person is shown.</summary>
    public string Describe() =>
        this == Requested ? "you asked for it to stop"
        : this == TaskClosedOut ? "the task it belongs to closed out"
        : this == WorktreeRemoved ? "the worktree it was standing in was removed"
        : this == ProcessGone ? "its process was already gone when the platform next looked"
        : "the reason was not recorded";

    public bool Equals(LocalLaunchStopReason? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class LocalLaunchStopReasonJsonConverter : JsonConverter<LocalLaunchStopReason>
    {
        public override LocalLaunchStopReason Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, LocalLaunchStopReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
