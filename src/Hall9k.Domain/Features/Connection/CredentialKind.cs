using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Connection;

/// <summary>How a connection's credential is sourced on the node.</summary>
[JsonConverter(typeof(CredentialKindJsonConverter))]
public sealed record CredentialKind
{
    /// <summary>Piggybacks the machine's gh CLI login; no identifier needed.</summary>
    public static readonly CredentialKind GhCli = new("gh-cli");
    public static readonly CredentialKind Keychain = new("keychain");
    public static readonly CredentialKind EnvironmentVariable = new("env");

    /// <summary>
    /// A secret Hall9k itself wrote to a file under the platform home, readable by the owner
    /// alone. It exists because the other three all point at a credential something else
    /// already holds — the gh CLI's login, an entry the human put in the keychain, a variable
    /// their shell exports — and a Jira API token pasted at a prompt is held by nobody yet.
    /// The reference still names a location rather than carrying the secret, which is the whole
    /// discipline (PLAN.md §10): the identifier is a file name under ~/.hall9k/credentials, and
    /// what an event payload records is that name.
    /// </summary>
    public static readonly CredentialKind File = new("file");

    /// <summary>Kind not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly CredentialKind Unknown = new("");

    public string Value { get; }

    private CredentialKind(string value) => Value = value;

    public static implicit operator string(CredentialKind? kind) => kind?.Value ?? string.Empty;

    public static implicit operator CredentialKind(string? value) => value.IsBlank() ? Unknown : new CredentialKind(value);

    public bool Equals(CredentialKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class CredentialKindJsonConverter : JsonConverter<CredentialKind>
    {
        public override CredentialKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, CredentialKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
