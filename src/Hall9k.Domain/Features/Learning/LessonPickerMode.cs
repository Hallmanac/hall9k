using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// What a node does with the lesson picker (<c>h9k config set --lesson-picker</c>): nothing
/// (<see cref="Off"/>), pick and record while every prompt stays as it was
/// (<see cref="Shadow"/>), or pick, record and let the picked set into the prompts that may carry it
/// (<see cref="On"/>). A closed vocabulary, so a sealed record with static instances rather than an
/// enum (TASK-MODEL.md section 8).
/// <para>
/// Shadow is the default, and it is also what a value nobody recognizes means: a hand-edited word the
/// platform cannot read must not be able to turn a live change into a prompt on its own, and it must
/// not switch the measurement off either. <see cref="Resolve"/> is the one place that turns the
/// configured word into a mode, so <c>h9k config show</c> and every composition read it identically.
/// </para>
/// </summary>
[JsonConverter(typeof(LessonPickerModeJsonConverter))]
public sealed record LessonPickerMode
{
    /// <summary>No picker session runs and no pick is recorded; prompts carry the newest lessons.</summary>
    public static readonly LessonPickerMode Off = new("off");

    /// <summary>The pick runs and is recorded; every prompt is byte-identical to the one composed with the setting off.</summary>
    public static readonly LessonPickerMode Shadow = new("shadow");

    /// <summary>The pick runs and is recorded, and prompts that may carry it do.</summary>
    public static readonly LessonPickerMode On = new("on");

    /// <summary>Not recognized. Never written to the config file and never recorded on an event.</summary>
    public static readonly LessonPickerMode Unknown = new("");

    /// <summary>The three settable words, in the order a refusal names them.</summary>
    public static readonly IReadOnlyList<LessonPickerMode> Settable = [Off, Shadow, On];

    public string Value { get; }

    private LessonPickerMode(string value) => Value = value;

    public static LessonPickerMode FromInput(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "off" => Off,
        "shadow" => Shadow,
        "on" => On,
        _ => Unknown,
    };

    public bool IsWellFormed => this == Off || this == Shadow || this == On;

    /// <summary>
    /// The mode a configured word means: nothing configured is shadow, a recognized word is itself,
    /// and anything else behaves as shadow.
    /// </summary>
    public static LessonPickerMode Resolve(string? configured)
    {
        LessonPickerMode parsed = FromInput(configured);
        return parsed.IsWellFormed ? parsed : Shadow;
    }

    /// <summary>True when a picker session may run at all (shadow or on).</summary>
    public bool Picks => this == Shadow || this == On;

    public static implicit operator string(LessonPickerMode? value) => value?.Value ?? string.Empty;

    public static implicit operator LessonPickerMode(string? value) => FromInput(value);

    public bool Equals(LessonPickerMode? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class LessonPickerModeJsonConverter : JsonConverter<LessonPickerMode>
    {
        public override LessonPickerMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, LessonPickerMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
