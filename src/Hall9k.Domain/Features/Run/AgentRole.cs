using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What shape of session is being spawned (Decisions Log #33). The roles are separately
/// configurable because they do different work: the build session writes a feature, the
/// review session reads far more than it writes, the fix session applies findings someone
/// else already reasoned out, and the synthesis session (Decisions Log #36) only condenses
/// text it is handed. Refinement is the (future) draft-refinement run
/// (backlog IDEA-draft-refinement-runs); its knob exists so that arriving does not have
/// to re-open this decision.
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
    /// An operator's own attached Claude Code session (h9k task work), not a spawned agent —
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
