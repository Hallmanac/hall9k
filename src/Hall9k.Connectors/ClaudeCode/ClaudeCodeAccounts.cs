using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Connectors.ClaudeCode;

/// <summary>Reads the claude-code connections this node records and turns a binding into the account a spawn runs under.</summary>
public static class ClaudeCodeAccounts
{
    /// <summary>
    /// The account a binding names: <see cref="ClaudeCodeAccount.Default"/> for null, else the connection's
    /// directory and observed identity. A binding that names no claude-code connection is refused rather than
    /// run under the default login, since falling back would bill the wrong account.
    /// </summary>
    public static async Task<ClaudeCodeAccount> LoadAsync(
        IQuerySession session, Guid? connectionId, CancellationToken cancellationToken)
    {
        if (connectionId is not { } id)
        {
            return ClaudeCodeAccount.Default;
        }

        ConnectionDetails? connection = await session.LoadAsync<ConnectionDetails>(id, cancellationToken);
        return connection is { } found && found.Provider == WorkItemProvider.ClaudeCode
            ? ClaudeCodeAccount.For(found)
            : throw new DomainNotFoundException(
                $"Claude Code connection {id} is not recorded on this node, so nothing bound to it can start. "
                + "Bind the project again with h9k project set <project> --claude-code-account <connection|default>.");
    }

    /// <summary>Every claude-code connection on this node, oldest first.</summary>
    public static async Task<IReadOnlyList<ConnectionDetails>> ListAsync(
        IQuerySession session, CancellationToken cancellationToken) =>
        await session.Query<ConnectionDetails>()
            // WorkItemProvider is a value object Marten cannot translate, so the filter is SQL against the stored
            // string, the house idiom (WorkItemConnections.FindJiraConnectionAsync).
            .Where(connection => connection.MatchesSql("d.data ->> 'provider' = ?", WorkItemProvider.ClaudeCode.Value))
            .OrderBy(connection => connection.RegisteredAt)
            .ToListAsync(cancellationToken);
}

/// <summary>Composes a child's environment for an account: the exact recorded directory for a bound one, and the variable removed for the default.</summary>
public static class ClaudeCodeEnvironment
{
    /// <summary>
    /// Applies <paramref name="account"/> to <paramref name="environment"/> (a <c>ProcessStartInfo.Environment</c>).
    /// The default login removes the variable rather than spelling out <c>~/.claude</c>: on macOS the login's
    /// Keychain item is a different one when the variable is set at all.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment, ClaudeCodeAccount account)
    {
        if (account.ConfigDirectory is { } directory)
        {
            environment[ClaudeConfigDirectory.Variable] = directory;
        }
        else
        {
            environment.Remove(ClaudeConfigDirectory.Variable);
        }
    }
}
