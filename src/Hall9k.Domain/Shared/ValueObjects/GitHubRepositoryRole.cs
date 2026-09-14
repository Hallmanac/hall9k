using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// A GitHub repository role, exactly as <c>gh</c> itself reports it — <c>viewerPermission</c> on
/// the repository object, or a collaborator's own <c>role_name</c> — normalized to lowercase so
/// the two endpoints' different casing (GraphQL's <c>ADMIN</c> against REST's <c>admin</c>) never
/// reads as two different roles. Value object per the house type discipline (TASK-MODEL.md §8):
/// the closed set is defined once, and an unrecognized value round-trips as itself rather than
/// failing, since a custom repository role GitHub Enterprise can name however an org likes is real
/// and this platform never needs to reject it, only to know it does not carry push.
/// </summary>
[JsonConverter(typeof(GitHubRepositoryRoleJsonConverter))]
public sealed record GitHubRepositoryRole
{
    public static readonly GitHubRepositoryRole Admin = new("admin");
    public static readonly GitHubRepositoryRole Maintain = new("maintain");
    public static readonly GitHubRepositoryRole Write = new("write");

    /// <summary>The REST collaborator endpoint's own name for the same access level as <see cref="Write"/> — the two never share a stored value, so a role read from that endpoint round-trips as "push" rather than being silently remapped, but <see cref="HasPush"/> still recognizes it.</summary>
    public static readonly GitHubRepositoryRole Push = new("push");
    public static readonly GitHubRepositoryRole Triage = new("triage");
    public static readonly GitHubRepositoryRole Read = new("read");
    public static readonly GitHubRepositoryRole None = new("none");

    /// <summary>Not recognized, or not yet observed. Serializes as an empty string.</summary>
    public static readonly GitHubRepositoryRole Unknown = new("");

    public string Value { get; }

    private GitHubRepositoryRole(string value) => Value = value;

    /// <summary>Whether this role grants push on the repository — write, maintain, and admin all do, and so does the REST collaborator endpoint's own "push" spelling of the same level; triage and read never do.</summary>
    public bool HasPush => this == Write || this == Maintain || this == Admin || this == Push;

    public static implicit operator string(GitHubRepositoryRole? role) => role?.Value ?? string.Empty;

    public static implicit operator GitHubRepositoryRole(string? value) =>
        value.IsBlank() ? Unknown : new GitHubRepositoryRole(value.Trim().ToLowerInvariant());

    public bool Equals(GitHubRepositoryRole? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class GitHubRepositoryRoleJsonConverter : JsonConverter<GitHubRepositoryRole>
    {
        public override GitHubRepositoryRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, GitHubRepositoryRole value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
