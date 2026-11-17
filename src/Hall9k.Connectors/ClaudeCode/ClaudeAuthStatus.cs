using System.Text.Json;

namespace Hall9k.Connectors.ClaudeCode;

/// <summary>
/// What <c>claude auth status</c> printed, read from its JSON (the default output on Claude Code 2.1.326).
/// It reads local state only and never touches the secret. A directory overridden by a credential in the
/// environment reports <see cref="LoggedIn"/> true with an <see cref="AuthMethod"/> naming the override and no
/// email or org id, which is why every check refuses a status without both.
/// </summary>
public sealed record ClaudeAuthStatus(
    bool LoggedIn,
    string? AuthMethod,
    string? Email,
    string? OrganizationId,
    string? OrganizationName,
    string? SubscriptionType,
    string? ApiKeySource)
{
    /// <summary>The status the JSON describes, or null when the text is not that JSON.</summary>
    public static ClaudeAuthStatus? Parse(string? json)
    {
        if (json is null || json.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out JsonElement loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return new ClaudeAuthStatus(
                loggedIn.GetBoolean(),
                Text(root, "authMethod"),
                Text(root, "email"),
                Text(root, "orgId"),
                Text(root, "orgName"),
                Text(root, "subscriptionType"),
                Text(root, "apiKeySource"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the status names the account itself, which an override credential never does.</summary>
    public bool NamesAnAccount =>
        Email is { Length: > 0 } && OrganizationId is { Length: > 0 };

    /// <summary>
    /// The variable the status gives as the source of an override credential: <c>apiKeySource</c> when it
    /// names one, <c>CLAUDE_CODE_OAUTH_TOKEN</c> for an OAuth token (which reports no source of its own),
    /// and otherwise the auth method itself.
    /// </summary>
    public string OverrideSource =>
        ApiKeySource is { Length: > 0 } source
            ? source
            : AuthMethod == "oauth_token"
                ? "CLAUDE_CODE_OAUTH_TOKEN"
                : AuthMethod is { Length: > 0 } method
                    ? $"the '{method}' credential"
                    : "a credential in the environment";

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
