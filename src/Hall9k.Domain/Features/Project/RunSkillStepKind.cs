using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// What running one step of a project's run skill actually takes (idea b9b09779, piece 5). A
/// closed vocabulary rather than a boolean, because the value is persisted on a launch's own
/// events and read back by a later <c>h9k task run-local --continue</c> in a different process:
/// a bool has no room for the <see cref="Unknown"/> an old or hand-edited event can carry, and
/// TASK-MODEL.md §8 puts every persisted closed vocabulary in this shape for exactly that reason.
/// </summary>
[JsonConverter(typeof(RunSkillStepKindJsonConverter))]
public sealed record RunSkillStepKind
{
    /// <summary>The step carries a command the platform can run on the worktree by itself.</summary>
    public static readonly RunSkillStepKind Command = new("command");

    /// <summary>
    /// The step carries no command, so somebody has to do it: obtain a credential, log into a
    /// service, install a runtime the skill only names. A launch stops here and prints it.
    /// </summary>
    public static readonly RunSkillStepKind Human = new("human");

    /// <summary>Not one of the two — an unparsed or unrecognized value off an old event.</summary>
    public static readonly RunSkillStepKind Unknown = new(string.Empty);

    /// <summary>Both real kinds, for validation and for a help line that names them.</summary>
    public static readonly IReadOnlyList<RunSkillStepKind> All = [Command, Human];

    public string Value { get; }

    private RunSkillStepKind(string value) => Value = value;

    public static implicit operator string(RunSkillStepKind? kind) => kind?.Value ?? string.Empty;

    /// <summary>Lenient mapping for a value already on a stream; unrecognized reads as <see cref="Unknown"/>.</summary>
    public static RunSkillStepKind FromInput(string? value) =>
        All.FirstOrDefault(kind => kind.Value == value?.Trim().ToLowerInvariant()) ?? Unknown;

    public bool Equals(RunSkillStepKind? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSkillStepKindJsonConverter : JsonConverter<RunSkillStepKind>
    {
        public override RunSkillStepKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, RunSkillStepKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
