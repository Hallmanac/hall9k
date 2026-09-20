using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// How much of a project's own event history the orchestrator feed admits (idea 89471598, piece
/// 2). Three nested bands, each a superset of the one before it, so a level is a floor rather
/// than a category: <see cref="Actionable"/> is what a person or another window is owed,
/// <see cref="Transitions"/> adds the work's own movement, and <see cref="Everything"/> adds the
/// machinery's. <see cref="OrchestratorFeedInterest"/> is the table that says which band each
/// event type sits in; this type is only the vocabulary and the ordering.
/// <para>
/// <see cref="Transitions"/> is both the default and an explicit choice — the
/// <see cref="Project.ClaimGate.Off"/> idiom rather than
/// <see cref="Project.AutoPrReviewSpeed"/>'s: nothing here behaves differently for a project that
/// never chose, because the middle band is what an orchestrator window wants whether or not
/// anybody has thought about it, so the platform has no use for telling the two apart.
/// </para>
/// </summary>
[JsonConverter(typeof(OrchestratorFeedLevelJsonConverter))]
public sealed record OrchestratorFeedLevel
{
    /// <summary>Only what somebody is owed: parks and disputes, gate and run failures, a merge
    /// that stays failed, daemon trouble, and any message from a person or another node's
    /// window.</summary>
    public static readonly OrchestratorFeedLevel Actionable = new("Actionable");

    /// <summary>The default: <see cref="Actionable"/> plus the work's own movement — task state
    /// changes, ideas logged or updated, and claims or takeovers involving another node.</summary>
    public static readonly OrchestratorFeedLevel Transitions = new("Transitions");

    /// <summary><see cref="Transitions"/> plus the machinery's own movement: a run's phase
    /// changes.</summary>
    public static readonly OrchestratorFeedLevel Everything = new("Everything");

    /// <summary>What a project that has never set this reads as — the middle band.</summary>
    public static readonly OrchestratorFeedLevel Default = Transitions;

    /// <summary>The whole vocabulary, narrowest band first — what <c>--help</c> lists.</summary>
    public static readonly IReadOnlyList<OrchestratorFeedLevel> All = [Actionable, Transitions, Everything];

    public string Value { get; }

    private OrchestratorFeedLevel(string value) => Value = value;

    /// <summary>
    /// How wide this band is, so <see cref="Admits"/> is a comparison rather than a second table
    /// to keep in step with the first. A value this build does not recognize reads at
    /// <see cref="Default"/>'s breadth rather than at zero or at nothing: a level nobody here
    /// understands should still hand an orchestrator its feed, not quietly narrow it to the
    /// smallest band (AGENTS.md, never guess at unobserved facts — "we do not know what this
    /// means" is not evidence the operator wanted less).
    /// </summary>
    public int Breadth => BreadthOf(Value);

    private static int BreadthOf(string value) => value switch
    {
        "Actionable" => 0,
        "Transitions" => 1,
        "Everything" => 2,
        _ => 1,
    };

    /// <summary>Whether a project reading at this level is handed an event the interest table
    /// classified at <paramref name="eventBand"/>. Nested bands, so this is a floor test: a
    /// project set to <see cref="Everything"/> sees an <see cref="Actionable"/> event too.</summary>
    public bool Admits(OrchestratorFeedLevel eventBand) => Breadth >= eventBand.Breadth;

    public static implicit operator string(OrchestratorFeedLevel? level) => level?.Value ?? Default.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="Project.ClaimGate"/> convention: a value
    /// built this way can carry anything, which is what lets
    /// <see cref="Project.Handlers.ProjectDecider.ChangeSettings"/> be the one place that actually
    /// enforces the closed set.
    /// </summary>
    public static implicit operator OrchestratorFeedLevel(string? value) =>
        value.IsBlank() ? Default : new OrchestratorFeedLevel(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as
    /// <see cref="Default"/>.</summary>
    public static OrchestratorFeedLevel FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "actionable" => Actionable,
        "transitions" => Transitions,
        "everything" => Everything,
        _ => Default,
    };

    /// <summary>The strict form a human's own input goes through: a typo is refused, never
    /// silently read as the default.</summary>
    public static OrchestratorFeedLevel Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank()
            ? Default
            : All.FirstOrDefault(level => level.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainValidationException(
                    $"'{RelayedLevel(trimmed)}' is not an orchestrator-feed level. Use actionable, "
                    + "transitions, or everything.");
    }

    /// <summary>
    /// What a refused level is safe to be quoted as — the <see cref="Project.AutoPrReviewSpeed"/>
    /// convention: this value comes off a command line and the refusal is printed to a terminal,
    /// so a control character or a bidirectional override in it cannot reach the refusal
    /// explaining it, and an unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedLevel(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(OrchestratorFeedLevel? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class OrchestratorFeedLevelJsonConverter : JsonConverter<OrchestratorFeedLevel>
    {
        public override OrchestratorFeedLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, OrchestratorFeedLevel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
