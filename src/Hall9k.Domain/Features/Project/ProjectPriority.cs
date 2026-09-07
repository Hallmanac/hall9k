using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Which tier a project's ready work competes in for a free dispatch slot (Decisions Log #141).
/// <see cref="Normal"/> is the default every project starts in and the only tier a
/// single-project install ever needs: within one tier the dispatcher rotates
/// (<c>Hall9k.Daemon.Dispatch.ProjectRotation</c>), so a tier nobody sets changes nothing at all.
/// <para>
/// A higher tier is <em>focus</em>, and focus is <em>self-releasing</em>: while a
/// <see cref="High"/> project has eligible work it wins every free slot ahead of every
/// lower-tier project, and the moment its queue drains the lower tiers resume with no operator
/// action. That is the whole distinction from a cap of 0 (<see cref="ProjectRunCeiling.IsPaused"/>),
/// which is deliberate and sticky and which nothing in the platform ever lifts on its own:
/// "drain A first, then carry on with B" is a tier, "never run B" is a pause, and neither lever
/// does the other's job.
/// </para>
/// <para>
/// A tier orders <em>who receives the next free slot</em> and nothing else. Nothing preempts: a
/// live run always completes, whatever tiers are set or changed mid-flight (AGENTS.md's
/// never-auto-kill restraint, the same rule <see cref="ProjectRunCeiling"/> holds to).
/// </para>
/// </summary>
[JsonConverter(typeof(ProjectPriorityJsonConverter))]
public sealed record ProjectPriority
{
    /// <summary>The default: this project rotates with every other normal-tier project.</summary>
    public static readonly ProjectPriority Normal = new("Normal");

    /// <summary>Focus: wins every free slot over lower tiers while it has eligible work, and releases itself when it drains.</summary>
    public static readonly ProjectPriority High = new("High");

    /// <summary>Background: takes a free slot only when no higher tier has eligible work.</summary>
    public static readonly ProjectPriority Low = new("Low");

    /// <summary>
    /// A recorded value this build does not recognize — a tier written by a newer version, or a
    /// hand-edited stream. Scheduled as <see cref="Normal"/> (<see cref="Tier"/> is identical), so
    /// an unreadable tier can neither starve a project nor silently promote one, and reported as
    /// what it is rather than as a tier somebody chose (AGENTS.md: never guess at unobserved facts).
    /// </summary>
    public static readonly ProjectPriority Unknown = new("Unknown");

    public string Value { get; }

    private ProjectPriority(string value) => Value = value;

    public static implicit operator string(ProjectPriority? priority) => priority?.Value ?? Normal.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="AutoPrReviewSpeed"/>/<see cref="BacklogPolicy"/>
    /// convention: a value built this way can carry anything, which is what lets
    /// <see cref="Handlers.ProjectDecider.ChangeSettings"/> be the one place that enforces the
    /// closed set. Blank is <see cref="Normal"/>, so a project that never recorded a tier and one
    /// explicitly set back to the default read identically.
    /// </summary>
    public static implicit operator ProjectPriority(string? value) =>
        value.IsBlank() ? Normal : new ProjectPriority(value);

    /// <summary>Lenient mapping for a value already on the stream; an unrecognized tier reads as <see cref="Unknown"/>.</summary>
    public static ProjectPriority FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => High,
        "normal" => Normal,
        "low" => Low,
        _ => Unknown,
    };

    /// <summary>
    /// The strict form a human's own input goes through: a typo is refused rather than silently
    /// scheduled as normal, because a mistyped focus that quietly does nothing is the one failure
    /// an operator would not notice. <c>default</c> is the clearing word — it restores
    /// <see cref="Normal"/>, the same idiom <c>--commit-style default</c> uses.
    /// </summary>
    public static ProjectPriority Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? Normal
            : FromInput(trimmed) is { } parsed && parsed != Unknown
                ? parsed
                : throw new DomainValidationException(
                    $"'{RelayedTier(trimmed)}' is not a project priority. Use high, normal, low, or default "
                    + "(which restores normal).");
    }

    /// <summary>
    /// Where this tier sits in the order the dispatcher compares — higher wins the free slot, and
    /// equal tiers rotate. An int rather than the ordering built into a list of instances, so the
    /// comparison is one expression a reader can check: <see cref="Unknown"/> deliberately shares
    /// <see cref="Normal"/>'s number, which is what keeps an unrecognized recorded value from
    /// changing anybody's schedule.
    /// </summary>
    public int Tier => Value == High.Value ? 1 : Value == Low.Value ? -1 : 0;

    /// <summary>
    /// Whether this tier is the default one — what an untouched project carries, and what makes a
    /// single-project install need no setting at all. <see cref="Unknown"/> is not it: it schedules
    /// like the default without being it, and an operator-facing surface says so.
    /// </summary>
    public bool IsDefault => Value == Normal.Value;

    /// <summary>
    /// What a refused tier is safe to be quoted as — the <see cref="AutoPrReviewSpeed"/>
    /// convention: the value comes off a command line and the refusal is printed to a terminal, so
    /// a control character or a bidirectional override in it cannot reach the refusal explaining
    /// it, and an unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedTier(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(ProjectPriority? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ProjectPriorityJsonConverter : JsonConverter<ProjectPriority>
    {
        public override ProjectPriority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ProjectPriority value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
