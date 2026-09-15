using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// A project member's role — owner or member only (idea 202383dc, T1; PLAN.md bearings: "Roles
/// are owner and member only"). Value object per the house type discipline (TASK-MODEL.md §8):
/// the closed set is defined once, serializes as its bare lowercase string — the same string the
/// ledger's own <c>members/&lt;root&gt;.yaml</c> file carries in its <c>role</c> field — and an
/// unrecognized value round-trips as itself rather than failing.
/// </summary>
[JsonConverter(typeof(ProjectMemberRoleJsonConverter))]
public sealed record ProjectMemberRole
{
    /// <summary>Holds the project's own gate: only an owner-role member's own chain can write a
    /// membership file or remove one.</summary>
    public static readonly ProjectMemberRole Owner = new("owner");

    /// <summary>Belongs to the project's team, but cannot itself vouch a new member.</summary>
    public static readonly ProjectMemberRole Member = new("member");

    /// <summary>Not recognized or not set. Serializes as an empty string.</summary>
    public static readonly ProjectMemberRole Unknown = new("");

    public string Value { get; }

    private ProjectMemberRole(string value) => Value = value;

    public static implicit operator string(ProjectMemberRole? role) => role?.Value ?? string.Empty;

    public static implicit operator ProjectMemberRole(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "owner" => Owner,
        "member" => Member,
        _ => Unknown,
    };

    public bool Equals(ProjectMemberRole? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ProjectMemberRoleJsonConverter : JsonConverter<ProjectMemberRole>
    {
        public override ProjectMemberRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ProjectMemberRole value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
