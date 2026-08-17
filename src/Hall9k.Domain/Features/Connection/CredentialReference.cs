using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// Structured pointer to where a credential lives — never the secret itself.
/// Canonical forms: "gh-cli", "keychain:&lt;name&gt;", "env:&lt;variable&gt;".
/// </summary>
[JsonConverter(typeof(CredentialReferenceJsonConverter))]
public sealed record CredentialReference(CredentialKind Kind, string? Identifier)
{
    public static readonly CredentialReference GhCli = new(CredentialKind.GhCli, null);

    public static CredentialReference Keychain(string name) => new(CredentialKind.Keychain, name);

    public static CredentialReference EnvironmentVariable(string name) => new(CredentialKind.EnvironmentVariable, name);

    public static CredentialReference Parse(string? value)
    {
        if (value.IsBlank())
        {
            return new CredentialReference(CredentialKind.Unknown, null);
        }

        int separator = value.IndexOf(':');
        return separator < 0
            ? new CredentialReference(value, null)
            : new CredentialReference(value[..separator], value[(separator + 1)..]);
    }

    public override string ToString() => Identifier.IsBlank() ? Kind.Value : $"{Kind.Value}:{Identifier}";

    private sealed class CredentialReferenceJsonConverter : JsonConverter<CredentialReference>
    {
        public override CredentialReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, CredentialReference value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
