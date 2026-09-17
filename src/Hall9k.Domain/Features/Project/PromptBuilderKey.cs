using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Which shipped prompt builder a project's own addendum is for (idea b9b09779, piece 6). A closed
/// vocabulary rather than a free string (TASK-MODEL.md §8): the daemon splices an addendum into a
/// fixed set of prompts it already composes, so the key space is exactly that set, not whatever a
/// caller happens to type. New members are added here as new prompt-composing components ship
/// (review-persona pieces of the same idea, for instance); nothing today speaks for a builder that
/// does not exist yet.
/// </summary>
[JsonConverter(typeof(PromptBuilderKeyJsonConverter))]
public sealed record PromptBuilderKey
{
    /// <summary>The work prompt, composed by <c>WorkPromptBuilder</c> — reached both by an
    /// interactive <c>h9k task work</c> and by the daemon's own fresh-dispatch build run (a task
    /// with no retry branch to resume composes through this same builder, not <see cref="Agent"/>),
    /// so this addendum reaches every headless build the daemon starts on its own, not only an
    /// interactively-claimed one.</summary>
    public static readonly PromptBuilderKey Work = new("work");

    /// <summary>The review-feedback lap prompt (<c>h9k pr review</c>), composed by <c>ReviewLapPromptBuilder</c>.</summary>
    public static readonly PromptBuilderKey ReviewLap = new("review-lap");

    /// <summary>
    /// Every daemon-dispatched review and lifecycle prompt <c>AgentPromptBuilder</c> composes that
    /// carries a <c>ProjectDetails</c> (follow-up, review, review-fix, and rebase) — one
    /// project-wide addendum spliced into each of them, rather than one per method, so a team
    /// states its house guidance for "an agent-dispatched review or fix session" once. Its five
    /// purely mechanical retry/recovery builders — <c>BuildStackAssessment</c>,
    /// <c>BuildBudgetRetry</c>, <c>BuildSessionErrorRetry</c>, <c>BuildUncommittedWorkRecovery</c>,
    /// <c>BuildContextSynthesis</c> — carry no project context today and get no addendum, left for
    /// whoever threads one through them.
    /// </summary>
    public static readonly PromptBuilderKey Agent = new("agent");

    /// <summary>The mention follow-up prompt, composed by <c>MentionFollowUpPromptBuilder</c>.</summary>
    public static readonly PromptBuilderKey MentionFollowUp = new("mention-follow-up");

    /// <summary>Not one of the builders above — an unparsed or unrecognized key.</summary>
    public static readonly PromptBuilderKey Unknown = new(string.Empty);

    /// <summary>Every real builder key, for <c>list</c> and for validating a caller's input against.</summary>
    public static readonly IReadOnlyList<PromptBuilderKey> All = [Work, ReviewLap, Agent, MentionFollowUp];

    public string Value { get; }

    private PromptBuilderKey(string value) => Value = value;

    public static implicit operator string(PromptBuilderKey? key) => key?.Value ?? string.Empty;

    /// <summary>
    /// The strict form CLI input goes through: blank or unrecognized is refused, naming every
    /// builder this project can actually address an addendum to.
    /// </summary>
    public static PromptBuilderKey Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        PromptBuilderKey? match = All.FirstOrDefault(key => key.Value == trimmed);
        if (match is null)
        {
            string known = string.Join(", ", All.Select(key => key.Value));
            throw new DomainValidationException(
                $"'{trimmed}' is not a prompt builder this project can address an addendum to. "
                + $"Known builders: {known}.");
        }

        return match;
    }

    public bool Equals(PromptBuilderKey? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PromptBuilderKeyJsonConverter : JsonConverter<PromptBuilderKey>
    {
        public override PromptBuilderKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return All.FirstOrDefault(key => key.Value == value) ?? Unknown;
        }

        public override void Write(Utf8JsonWriter writer, PromptBuilderKey value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
