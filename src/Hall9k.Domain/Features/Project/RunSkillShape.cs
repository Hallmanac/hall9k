using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Which of the three shapes a project's run skill is (idea b9b09779, piece 4). A closed
/// vocabulary rather than a free string (TASK-MODEL.md §8): the skill's own first line states its
/// shape, and a reader — a QA or design review session about to stand the project up, or an
/// orchestrator — decides what to do next from it, so the shape is exactly these three, never
/// whatever prose a composing session felt like writing.
/// <para>
/// <see cref="Pointer"/> and <see cref="FullText"/> are the composing agent's own call, made from
/// what the repository actually carries. <see cref="NoneDiscoverable"/> is not: it is what the
/// daemon records mechanically when its own survey of the repository finds nothing at all to read
/// (<c>RunSkillRepositorySurvey</c>), before any session is dispatched — tools before tokens.
/// </para>
/// </summary>
[JsonConverter(typeof(RunSkillShapeJsonConverter))]
public sealed record RunSkillShape
{
    /// <summary>
    /// The repository already carries skills or documentation that cover launching it, so the run
    /// skill points at them by path and adds only what they leave out.
    /// </summary>
    public static readonly RunSkillShape Pointer = new("pointer");

    /// <summary>
    /// The repository carries no such coverage, so the run skill holds the whole procedure itself
    /// and cites the files each step derives from.
    /// </summary>
    public static readonly RunSkillShape FullText = new("full-text");

    /// <summary>
    /// Nothing in the repository says how to run it. The skill says so plainly and
    /// <c>h9k project show</c> reads "run skill: none discoverable" rather than nothing.
    /// </summary>
    public static readonly RunSkillShape NoneDiscoverable = new("none-discoverable");

    /// <summary>Not one of the three — an unparsed or unrecognized value off an old event.</summary>
    public static readonly RunSkillShape Unknown = new(string.Empty);

    /// <summary>Every real shape, for validating input and for a help line that names them all.</summary>
    public static readonly IReadOnlyList<RunSkillShape> All = [Pointer, FullText, NoneDiscoverable];

    public string Value { get; }

    private RunSkillShape(string value) => Value = value;

    public static implicit operator string(RunSkillShape? shape) => shape?.Value ?? string.Empty;

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as <see cref="Unknown"/>.</summary>
    public static RunSkillShape FromInput(string? value) =>
        All.FirstOrDefault(shape => shape.Value == value?.Trim().ToLowerInvariant()) ?? Unknown;

    /// <summary>The strict form a human's or a session's own input goes through: a typo is refused, never read as one of the three.</summary>
    public static RunSkillShape Parse(string? value)
    {
        RunSkillShape parsed = FromInput(value);
        return parsed == Unknown
            ? throw new DomainValidationException(
                $"'{value?.Trim()}' is not a run-skill shape. Use {string.Join(", ", All.Select(shape => shape.Value))}.")
            : parsed;
    }

    public bool Equals(RunSkillShape? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSkillShapeJsonConverter : JsonConverter<RunSkillShape>
    {
        public override RunSkillShape Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, RunSkillShape value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
