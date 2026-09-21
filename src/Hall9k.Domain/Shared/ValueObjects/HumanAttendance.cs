using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Whether a human was at the session that recorded a decision or a lesson (idea d805fd8b,
/// piece 1). Three values, not two, because AGENTS.md's never-guess rule applies here as much as
/// to any other audit field: the platform can see whether a run's own claim and session name are
/// an operator's attached ones, and it cannot see who is at the keyboard of a plain shell. The
/// third value says so rather than filling in the plausible answer.
/// </summary>
[JsonConverter(typeof(HumanAttendanceJsonConverter))]
public sealed record HumanAttendance
{
    /// <summary>Recorded from inside a run held by an operator's own attached session (h9k task work).</summary>
    public static readonly HumanAttendance Attended = new("Attended");

    /// <summary>Recorded from inside a run nobody is attached to: a dispatched agent, a deliberate headless start, or a delegated contractor.</summary>
    public static readonly HumanAttendance Unattended = new("Unattended");

    /// <summary>
    /// Nothing was observed either way — no run was named at all (the ordinary shell call), or the
    /// run that was named records no session this platform can read attendance off. Never a
    /// synonym for <see cref="Attended"/>: <c>DecisionDecider</c> refuses a decision recorded
    /// against a run unless attendance was positively observed.
    /// </summary>
    public static readonly HumanAttendance Unobserved = new("Unobserved");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly HumanAttendance Unknown = new("");

    public string Value { get; }

    private HumanAttendance(string value) => Value = value;

    public static implicit operator string(HumanAttendance? attendance) => attendance?.Value ?? string.Empty;

    public static implicit operator HumanAttendance(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : new HumanAttendance(value);

    public bool Equals(HumanAttendance? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class HumanAttendanceJsonConverter : JsonConverter<HumanAttendance>
    {
        public override HumanAttendance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, HumanAttendance value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
