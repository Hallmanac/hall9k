using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether true closeout closes a task's linked GitHub issue, and when (task: a task's linked
/// GitHub issue is closed at true closeout under a configurable rule). An issue is not always one
/// unit of work — an epic, a PRD, an ADR, or an issue split into several tasks — so closing it the
/// moment any one task merges would be wrong exactly as often as leaving it open forever would be,
/// which is why this is a project default with a per-task override rather than a platform-wide
/// behavior (origin: Brian walked the many-tasks-one-issue case 2026-09-06, deciding it is knowable
/// from the store — several tasks carrying the same <c>ExternalReference</c> — rather than needing
/// its own configuration).
/// <para>
/// <see cref="WhenAllTasksClose"/> is the default a new project starts in: closing waits for every
/// task linked to the same issue to itself reach true closeout or be abandoned, decided fresh at
/// the LAST one to do so — an explicit override on any single sibling beats an inherited default on
/// the rest, which is what makes one override on one task enough to change the outcome for the
/// whole issue.
/// </para>
/// <para>
/// Jira is untouched: this rule applies to
/// <see cref="Hall9k.Domain.Shared.ValueObjects.WorkItemProvider.GitHub"/> alone. A Jira card's
/// merge comment behaviour does not change (Brian's design, 2026-09-06) — closing a card is a
/// workflow transition this platform still refuses to guess at for Jira, the same restraint
/// <see cref="ClaimGate"/> and every write in this codebase already give a tracker's own workflow.
/// </para>
/// </summary>
[JsonConverter(typeof(CloseLinkedIssueRuleJsonConverter))]
public sealed record CloseLinkedIssueRule
{
    /// <summary>Close the issue in the same step that posts the merge note, the moment this task reaches true closeout.</summary>
    public static readonly CloseLinkedIssueRule OnCloseout = new("OnCloseout");

    /// <summary>Post the merge note and never close the issue — the right choice for an epic, a PRD, or an ADR.</summary>
    public static readonly CloseLinkedIssueRule Never = new("Never");

    /// <summary>
    /// The default: post the merge note every time, and close the issue only once every task
    /// linked to it has itself reached true closeout or been abandoned — decided fresh at the last
    /// one, across every linked task's own recorded rule (an explicit override on any one of them
    /// beats an inherited default on the rest).
    /// </summary>
    public static readonly CloseLinkedIssueRule WhenAllTasksClose = new("WhenAllTasksClose");

    /// <summary>
    /// A recorded value this build does not recognize — a rule written by a newer version, or a
    /// hand-edited stream. Read as <see cref="Never"/> for the purpose of deciding whether to close
    /// anything: closing an issue is the one act this whole feature exists to gate carefully, so an
    /// unreadable rule fails toward leaving it open rather than toward closing it (AGENTS.md: never
    /// guess at unobserved facts).
    /// </summary>
    public static readonly CloseLinkedIssueRule Unknown = new("Unknown");

    public string Value { get; }

    private CloseLinkedIssueRule(string value) => Value = value;

    public static implicit operator string(CloseLinkedIssueRule? rule) => rule?.Value ?? WhenAllTasksClose.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="ProjectPriority"/> convention: a value built
    /// this way can carry anything, which is what lets
    /// <see cref="Handlers.ProjectDecider.ChangeSettings"/> be the one place that enforces the
    /// closed set. Blank is <see cref="WhenAllTasksClose"/>, so a project that never recorded a
    /// rule and one explicitly set back to the default read identically.
    /// </summary>
    public static implicit operator CloseLinkedIssueRule(string? value) =>
        value.IsBlank() ? WhenAllTasksClose : new CloseLinkedIssueRule(value);

    /// <summary>Lenient mapping for a value already on the stream; an unrecognized rule reads as <see cref="Unknown"/>.</summary>
    public static CloseLinkedIssueRule FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on-closeout" => OnCloseout,
        "never" => Never,
        "when-all-tasks-close" => WhenAllTasksClose,
        _ => Unknown,
    };

    /// <summary>
    /// The strict form a project's own input goes through: a typo is refused rather than silently
    /// scheduled as the default. <c>default</c> is the clearing word — it restores
    /// <see cref="WhenAllTasksClose"/>, the same idiom <c>--priority default</c> uses.
    /// </summary>
    public static CloseLinkedIssueRule Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? WhenAllTasksClose
            : FromInput(trimmed) is { } parsed && parsed != Unknown
                ? parsed
                : throw new DomainValidationException(
                    $"'{RelayedRule(trimmed)}' is not a close-linked-issue rule. Use on-closeout, never, "
                    + "when-all-tasks-close, or default (which restores when-all-tasks-close).");
    }

    /// <summary>
    /// The strict form a TASK-level override's own input goes through: unlike <see cref="Parse"/>,
    /// blank or <c>default</c> means "no override" (null) rather than a concrete rule — a task
    /// with no override of its own defers to whatever the project currently says, live, not to a
    /// value frozen at the moment the override was cleared (the same present-with-null-clears
    /// idiom <c>TaskRevised.ReviewStageComposition</c> already uses).
    /// </summary>
    public static CloseLinkedIssueRule? ParseOverride(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank() || trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return FromInput(trimmed) is { } parsed && parsed != Unknown
            ? parsed
            : throw new DomainValidationException(
                $"'{RelayedRule(trimmed)}' is not a close-linked-issue rule. Use on-closeout, never, "
                + "when-all-tasks-close, or default (which clears this task's own override and defers "
                + "to the project's close-linked-issue setting).");
    }

    /// <summary>
    /// What a refused rule is safe to be quoted as — the <see cref="ProjectPriority"/> convention:
    /// this value comes off a command line and the refusal is printed to a terminal, so a control
    /// character or a bidirectional override in it cannot reach the refusal explaining it, and an
    /// unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedRule(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(CloseLinkedIssueRule? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class CloseLinkedIssueRuleJsonConverter : JsonConverter<CloseLinkedIssueRule>
    {
        public override CloseLinkedIssueRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, CloseLinkedIssueRule value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
