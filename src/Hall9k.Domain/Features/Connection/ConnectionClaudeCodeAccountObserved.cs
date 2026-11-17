namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// Who a claude-code connection's directory is logged in as, as <c>claude auth status</c> reported it, run
/// with <c>CLAUDE_CONFIG_DIR</c> set to the recorded directory. Every spawn under the connection is
/// checked against <see cref="Email"/> and <see cref="OrganizationId"/>, so a directory that has since
/// been logged into a different account is refused rather than billed. Node-scoped like every connection
/// event: the directory and the email are this machine's own and never replicate.
/// </summary>
public sealed record ConnectionClaudeCodeAccountObserved(
    Guid Id,
    string Email,
    string OrganizationId,
    string? OrganizationName,
    string? SubscriptionType,
    DateTimeOffset ObservedAt);
