using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What shape of session is being spawned (Decisions Log #33). The roles are separately
/// configurable because they do different work: the build session writes a feature, the
/// review session reads far more than it writes, the fix session applies findings someone
/// else already reasoned out. Refinement is the (future) draft-refinement run
/// (backlog IDEA-draft-refinement-runs); its knob exists so that arriving does not have
/// to re-open this decision.
/// </summary>
[JsonConverter(typeof(AgentRoleJsonConverter))]
public sealed record AgentRole
{
    public static readonly AgentRole Build = new("Build");
    public static readonly AgentRole Review = new("Review");
    public static readonly AgentRole Fix = new("Fix");
    public static readonly AgentRole Refinement = new("Refinement");
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
