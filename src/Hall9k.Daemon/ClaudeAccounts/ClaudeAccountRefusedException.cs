namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>
/// A spawn under a bound Claude Code account was refused because the account's check failed; no process
/// started. The message is the check's own text, naming the directory and the fix. A run's spawn site holds
/// the run on this (<see cref="ClaudeAccountHolds"/>); every other site records it as the session's
/// could-not-start outcome, which it already does for any exception from a spawn.
/// </summary>
public sealed class ClaudeAccountRefusedException(Guid connectionId, string message) : InvalidOperationException(message)
{
    public Guid ConnectionId { get; } = connectionId;
}
