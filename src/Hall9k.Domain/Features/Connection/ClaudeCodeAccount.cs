using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// The Claude Code account a spawn runs under: the node's default login (<see cref="Default"/>, no
/// connection, and <c>CLAUDE_CONFIG_DIR</c> removed from the child's environment) or a claude-code
/// connection's directory together with the email and org id observed for it. Every spawn states one,
/// and a bound one is checked against what <c>claude auth status</c> reports before any process starts.
/// </summary>
public sealed record ClaudeCodeAccount(
    Guid? ConnectionId,
    string? ConfigDirectory,
    string? Email,
    string? OrganizationId)
{
    public static readonly ClaudeCodeAccount Default = new(null, null, null, null);

    public bool IsDefault => ConnectionId is null;

    /// <summary>The account a claude-code connection names, or <see cref="Default"/> for anything else.</summary>
    public static ClaudeCodeAccount For(ConnectionDetails? connection) =>
        connection is { } bound && bound.Provider == WorkItemProvider.ClaudeCode
            ? new ClaudeCodeAccount(
                bound.Id, bound.ExternalAccountId, bound.ClaudeCodeEmail, bound.ClaudeCodeOrganizationId)
            : Default;

    /// <summary>How a person reads this account: the observed email and directory, or "the default login".</summary>
    public string Describe() =>
        IsDefault
            ? "the default Claude Code login"
            : $"{Email ?? "an unobserved account"} ({ConfigDirectory})";
}
