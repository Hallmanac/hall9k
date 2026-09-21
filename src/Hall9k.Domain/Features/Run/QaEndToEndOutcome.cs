using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What happened when a QA review ran the project's end-to-end tests on the review worktree
/// (idea b9b09779, piece 2). Three observations and a sentinel for "the session never said",
/// because all four are different facts and only three of them are observations.
/// <para>
/// <see cref="Absent"/> is the one worth spelling out: a project with no end-to-end suite to run
/// is not a passing one, and a report that rendered the two identically would be claiming a
/// green run nobody ever saw. It is also, on its own, the most useful thing a QA review can tell
/// a team that thinks it has coverage.
/// </para>
/// </summary>
[JsonConverter(typeof(QaEndToEndOutcomeJsonConverter))]
public sealed record QaEndToEndOutcome
{
    /// <summary>The end-to-end tests ran on this worktree and passed.</summary>
    public static readonly QaEndToEndOutcome Pass = new("pass");

    /// <summary>They ran and something failed. The report carries the failure's own output as evidence.</summary>
    public static readonly QaEndToEndOutcome Fail = new("fail");

    /// <summary>This project has no end-to-end tests to run, so nothing was observed either way.</summary>
    public static readonly QaEndToEndOutcome Absent = new("absent");

    /// <summary>The session never answered — no marker, or a word nobody could read. Serializes as the empty string.</summary>
    public static readonly QaEndToEndOutcome Unstated = new("");

    public string Value { get; }

    private QaEndToEndOutcome(string value) => Value = value;

    /// <summary>True when the session actually answered.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>The tolerant read — anything else is <see cref="Unstated"/>, never a guess.</summary>
    public static QaEndToEndOutcome Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" or "passing" => Pass,
        "fail" or "failed" or "failing" => Fail,
        "absent" or "none" => Absent,
        _ => Unstated,
    };

    /// <summary>How the outcome reads in a findings report, so every report words it identically.</summary>
    public string Describe() =>
        this == Pass ? "ran on the review worktree and passed"
        : this == Fail ? "ran on the review worktree and failed; the evidence is in the report below"
        : this == Absent ? "this project has none to run, so nothing was observed"
        : "not reported by this session";

    public static implicit operator string(QaEndToEndOutcome? value) => value?.Value ?? string.Empty;

    public bool Equals(QaEndToEndOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class QaEndToEndOutcomeJsonConverter : JsonConverter<QaEndToEndOutcome>
    {
        public override QaEndToEndOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, QaEndToEndOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
