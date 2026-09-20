using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// A review pass's answer to the one standing question every review carries (idea b9b09779,
/// piece 1): did this change alter how the application runs locally?
/// <para>
/// The question exists because the instructions for running a project drift silently. Nothing
/// fails when a new environment variable, a new service, or a changed startup command lands and
/// the run instructions do not; the next person to run it locally simply finds them wrong. Asking
/// it of every pass, and recording the answer either way, is what turns that silence into a
/// finding — <see cref="ReviewFindingKind.RunSkillDrift"/> for a yes, and a recorded
/// <see cref="No"/> so a report can show the question was actually asked rather than merely
/// having been in the prompt.
/// </para>
/// </summary>
[JsonConverter(typeof(RunSkillDriftAnswerJsonConverter))]
public sealed record RunSkillDriftAnswer
{
    /// <summary>The change altered how the application runs locally, and the pass filed a run-skill drift finding saying how.</summary>
    public static readonly RunSkillDriftAnswer Yes = new("yes");

    /// <summary>Checked, and nothing about running the application locally changed.</summary>
    public static readonly RunSkillDriftAnswer No = new("no");

    /// <summary>
    /// The pass never answered — no marker, or a word nobody could read. Distinct from
    /// <see cref="No"/> on purpose: "nobody asked" and "asked and answered no" are different
    /// facts, and a report that printed the second when only the first happened would be
    /// claiming an observation nobody made. Serializes as the empty string.
    /// </summary>
    public static readonly RunSkillDriftAnswer Unstated = new("");

    public string Value { get; }

    private RunSkillDriftAnswer(string value) => Value = value;

    /// <summary>True when the pass actually answered, either way.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>The tolerant read — anything else is <see cref="Unstated"/>, never a guess.</summary>
    public static RunSkillDriftAnswer Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "yes." => Yes,
        "no" or "n" or "none" or "no." => No,
        _ => Unstated,
    };

    /// <summary>How the answer reads in a findings report, so every report words it identically.</summary>
    public string Describe() =>
        this == Yes ? "yes — this change altered how the application runs locally; see the run-skill drift finding"
        : this == No ? "checked, no — nothing about running the application locally changed"
        : "not answered by this pass";

    public static implicit operator string(RunSkillDriftAnswer? value) => value?.Value ?? string.Empty;

    public bool Equals(RunSkillDriftAnswer? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSkillDriftAnswerJsonConverter : JsonConverter<RunSkillDriftAnswer>
    {
        public override RunSkillDriftAnswer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, RunSkillDriftAnswer value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
