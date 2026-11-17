using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// The Claude Code account an interactive session was observed running under, as exactly one of three
/// values: a claude-code connection on this node (by id), the default login, or <see cref="Unrecognized"/>,
/// a config directory no connection records. Never a path or an email: it rides on the node-scoped
/// <c>InteractiveSessionStarted</c>, and the directory is this machine's own.
/// <para>
/// It decides whether <c>h9k task drive</c> may offer to resume the session: a conversation lives in the
/// transcripts of the directory that started it, so only a session on the project's current binding can be
/// found by a resume line carrying that binding, and an unrecognized one never can.
/// </para>
/// </summary>
[JsonConverter(typeof(ClaudeCodeSessionAccountJsonConverter))]
public sealed record ClaudeCodeSessionAccount
{
    public static readonly ClaudeCodeSessionAccount Default = new(null, "default");

    /// <summary>A config directory that names no claude-code connection on this node, or a stored value this build cannot read.</summary>
    public static readonly ClaudeCodeSessionAccount Unrecognized = new(null, "unrecognized");

    public Guid? ConnectionId { get; }

    public string Value { get; }

    private ClaudeCodeSessionAccount(Guid? connectionId, string value)
    {
        ConnectionId = connectionId;
        Value = value;
    }

    public static ClaudeCodeSessionAccount Connection(Guid connectionId) => new(connectionId, connectionId.ToString());

    /// <summary>The account a spawn ran under: its connection, or the default login.</summary>
    public static ClaudeCodeSessionAccount For(ClaudeCodeAccount account) => account.ConnectionId is { } id
        ? Connection(id)
        : Default;

    /// <summary>
    /// The account a session reporting <paramref name="configDirectoryVariable"/> as its <c>CLAUDE_CONFIG_DIR</c> runs
    /// under: unset or blank is the default login, a directory equal under the connection path rule
    /// (<see cref="ClaudeConfigDirectory.Normalize"/>) to a claude-code connection's recorded one is that
    /// connection, and anything else, a path the rule refuses included, is <see cref="Unrecognized"/>.
    /// </summary>
    public static ClaudeCodeSessionAccount Derive(
        string? configDirectoryVariable, IEnumerable<ConnectionDetails> connections, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectoryVariable))
        {
            return Default;
        }

        string normalized;
        try
        {
            normalized = ClaudeConfigDirectory.Normalize(configDirectoryVariable, homeDirectory);
        }
        catch (DomainValidationException)
        {
            return Unrecognized;
        }

        return connections.FirstOrDefault(connection =>
                connection.Provider == WorkItemProvider.ClaudeCode
                && string.Equals(connection.ExternalAccountId, normalized, StringComparison.Ordinal)) is { } matched
            ? Connection(matched.Id)
            : Unrecognized;
    }

    /// <summary>
    /// Whether a session under this account can be resumed by a line carrying <paramref name="binding"/>, the
    /// project's current claude-code connection or null for the default login. An unrecognized account never can.
    /// </summary>
    public bool Matches(Guid? binding) => this != Unrecognized && ConnectionId == binding;

    public override string ToString() => Value;

    private sealed class ClaudeCodeSessionAccountJsonConverter : ClosedVocabularyJsonConverter<ClaudeCodeSessionAccount>
    {
        protected override ClaudeCodeSessionAccount FromStored(string? value) => value switch
        {
            null or "" or "default" => Default,
            _ when Guid.TryParse(value, out Guid id) => Connection(id),
            _ => Unrecognized,
        };

        protected override string ToStored(ClaudeCodeSessionAccount value) => value.Value;
    }
}
