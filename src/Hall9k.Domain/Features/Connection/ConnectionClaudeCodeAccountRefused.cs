namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// A claude-code connection's check failed (its directory is missing, logged out, logged into a different
/// account, or outranked by an override credential), so nothing starts under it until a check passes.
/// <see cref="Reason"/> is the text <c>h9k status</c> prints, naming the directory and the fix. The
/// daemon re-checks a refused account on its own and records <see cref="ConnectionClaudeCodeAccountCleared"/>
/// when one passes.
/// </summary>
public sealed record ConnectionClaudeCodeAccountRefused(
    Guid Id,
    string Reason,
    DateTimeOffset RefusedAt);
