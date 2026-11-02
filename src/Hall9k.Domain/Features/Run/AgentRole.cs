using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What shape of session is being spawned (Decisions Log #33). The roles are separately
/// configurable because they do different work: the build session writes a feature, the
/// review session reads far more than it writes, the fix session applies findings someone
/// else already reasoned out, and the synthesis session (Decisions Log #36) only condenses
/// text it is handed. Refinement is the headless session a plan (<c>h9k plan start</c>)
/// dispatches to refine a draft; it follows the project's model, then the node's role value,
/// then the node's default, and a draft's own model does not move it.
/// </summary>
[JsonConverter(typeof(AgentRoleJsonConverter))]
public sealed record AgentRole
{
    public static readonly AgentRole Build = new("Build");
    public static readonly AgentRole Review = new("Review");
    public static readonly AgentRole Fix = new("Fix");
    /// <summary>Condenses a fan-in of blocker handoffs into one context document (Decisions Log #36).</summary>
    public static readonly AgentRole Synthesis = new("Synthesis");
    public static readonly AgentRole Refinement = new("Refinement");
    /// <summary>
    /// An operator's own attached Claude Code session (h9k task drive), not a spawned agent —
    /// its own role so a reader can tell an interactive claim's process from a headless one's on
    /// <see cref="Projections.RunDetails.ActiveSessions"/> rather than conflating the two under Build.
    /// </summary>
    public static readonly AgentRole Interactive = new("Interactive");
    /// <summary>
    /// Writes a task up as a card in an external tracker (backlog 18). Its own role because it
    /// is the one session that writes nothing to the repository and reads almost none of it: its
    /// work is the project's card-authoring skill and one <c>h9k task write-jira</c> command at
    /// the end — it makes no Jira call itself, through MCP or otherwise (Decisions Log #102).
    /// </summary>
    public static readonly AgentRole Publication = new("Publication");
    /// <summary>
    /// Delivers a project's orchestrator feed to its live orchestrator session and exits (idea
    /// 89471598, piece 3) — a run with no task, unlike every role above. Its own role because
    /// nothing else on this list runs with no task to record itself against, and because the
    /// courier's own model default sits underneath a floor of its own (<c>DaemonOptions.
    /// ResolveCourierModel</c>) rather than the platform's <c>DefaultModel</c>.
    /// </summary>
    public static readonly AgentRole Courier = new("Courier");
    /// <summary>
    /// The pull-request review pre-flight (idea 6be68ee2, finding 1, phase one) — a short, no-
    /// checkout security lap over a pull request's own changed-file list and diff hunks, run on
    /// its own stream before any worktree exists (the courier's own run-with-no-task precedent).
    /// Its own role for the identical reason <see cref="Courier"/> is: the pre-flight's own model
    /// default sits underneath a floor of its own (<c>DaemonOptions.ResolveSecurityPreflightModel</c>)
    /// rather than the platform's <c>DefaultModel</c>, because it reads attacker-written text
    /// before any permission scoping to a real checkout exists.
    /// </summary>
    public static readonly AgentRole SecurityPreflight = new("SecurityPreflight");
    /// <summary>
    /// The independent challenge of a draft (<c>h9k task challenge</c>): one headless session that tests
    /// the draft's claims against the code before it publishes. Its own role, and not
    /// <see cref="Refinement"/> reused, because it has a floor of its own (<c>DaemonOptions.ResolveChallengeModel</c>
    /// and <c>ResolveChallengeEffort</c>) that must hold on a node whose default model or node-wide effort
    /// is cheaper, while refinement itself follows those.
    /// </summary>
    public static readonly AgentRole Challenge = new("Challenge");
    /// <summary>
    /// The feedback step's check (a task's blockers left notes; do they affect it?): one headless, no-tools session that reads
    /// the notes and the task and answers yes or no. Its own role, and not <see cref="Refinement"/> reused, so the cheap check
    /// and the high-tier refinement it may lead to are set apart in the model policy without a code change.
    /// </summary>
    public static readonly AgentRole FeedbackCheck = new("FeedbackCheck");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly AgentRole Unknown = new("");

    public string Value { get; }

    private AgentRole(string value) => Value = value;

    public static implicit operator string(AgentRole? role) => role?.Value ?? string.Empty;

    public static implicit operator AgentRole(string? value) => value.IsBlank() ? Unknown : new AgentRole(value);

    public bool Equals(AgentRole? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class AgentRoleJsonConverter : JsonConverter<AgentRole>
    {
        public override AgentRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, AgentRole value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
