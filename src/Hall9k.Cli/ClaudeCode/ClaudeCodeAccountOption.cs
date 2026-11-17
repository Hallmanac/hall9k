using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.ClaudeCode;

/// <summary>
/// Reads <c>h9k project set --claude-code-account</c>: <c>default</c> clears the binding, and anything else
/// must name a claude-code connection recorded on this node, by its id (full or the short form
/// <c>h9k connection list</c> prints) or by its directory, normalized by the same path rule the connection
/// was recorded under. Pure over the node's connections, so every refusal is testable without a database.
/// </summary>
public static class ClaudeCodeAccountOption
{
    /// <summary>The word that clears a binding back to the default login.</summary>
    public const string DefaultWord = "default";

    /// <summary>
    /// The connection id <paramref name="value"/> binds, or null for <c>default</c>. Refuses a value that names
    /// no connection, one that names a connection of another provider, and a short id more than one matches.
    /// </summary>
    public static Guid? Resolve(string value, IReadOnlyList<ConnectionDetails> connections, string homeDirectory)
    {
        string trimmed = value.Trim();
        if (string.Equals(trimmed, DefaultWord, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ConnectionDetails[] matches = LooksLikePath(trimmed)
            ? [.. connections.Where(connection =>
                connection.Provider == WorkItemProvider.ClaudeCode
                && connection.ExternalAccountId == ClaudeConfigDirectory.Normalize(trimmed, homeDirectory))]
            : Guid.TryParse(trimmed, out Guid id)
                ? [.. connections.Where(connection => connection.Id == id)]
                : [.. connections.Where(connection =>
                    string.Equals(DomainId.Short(connection.Id), trimmed, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [{ } only] when only.Provider == WorkItemProvider.ClaudeCode => only.Id,
            [{ } other] => throw new DomainValidationException(
                $"Connection {DomainId.Short(other.Id)} is a '{other.Provider.Value}' connection, not a claude-code one, "
                + "so no Claude Code session can run under it. h9k connection list shows the claude-code connections "
                + "on this node."),
            [] => throw new DomainNotFoundException(
                $"No claude-code connection on this node matches '{trimmed}'. Register the directory first with "
                + "h9k connection add claude-code --config-dir ~/.claude-alt, then bind it by the id h9k connection list "
                + $"prints, or pass '{DefaultWord}' to run the project under the default login."),
            _ => throw new DomainValidationException(
                $"'{trimmed}' matches more than one connection on this node; pass the full id from h9k connection list."),
        };
    }

    private static bool LooksLikePath(string value) =>
        value.StartsWith('~') || value.Contains('/') || value.Contains('\\') || Path.IsPathFullyQualified(value);
}
