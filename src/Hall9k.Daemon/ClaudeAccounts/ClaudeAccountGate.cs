using Hall9k.Connectors.ClaudeCode;
using Hall9k.Domain.Features.Connection;
using Marten;

namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>Where the gate records an account's refused state: the connection stream in the daemon, a fake in tests.</summary>
public interface IClaudeAccountRefusalLedger
{
    Task RecordAsync(Guid connectionId, ClaudeAccountCheckResult result, CancellationToken cancellationToken);
}

/// <summary>
/// The daemon's side of the pre-spawn check: runs <see cref="ClaudeAccountChecker"/> for a bound account and
/// records the account's refused state, so <c>h9k status</c> shows one needs-you line per refused connection
/// and the engines that start new work skip its projects. A refusal throws
/// <see cref="ClaudeAccountRefusedException"/>; nothing ever falls back to the default login.
/// </summary>
public sealed class ClaudeAccountGate(IClaudeAccountRefusalLedger ledger, ClaudeAccountChecker checker)
{
    /// <summary>Refuses a spawn under <paramref name="account"/> unless its check passes; a recent pass is reused.</summary>
    public async Task EnsureUsableAsync(ClaudeCodeAccount account, CancellationToken cancellationToken)
    {
        if (account.ConnectionId is not { } connectionId)
        {
            return;
        }

        ClaudeAccountCheckResult result = await checker.CheckAsync(account, reusePass: true, cancellationToken);
        await RecordAsync(connectionId, result, cancellationToken);
        if (!result.Passed)
        {
            throw new ClaudeAccountRefusedException(connectionId, result.Message);
        }
    }

    /// <summary>Checks <paramref name="account"/> as it is now, ignoring any recent pass, and records the result.</summary>
    public async Task<ClaudeAccountCheckResult> RecheckAsync(ClaudeCodeAccount account, CancellationToken cancellationToken)
    {
        ClaudeAccountCheckResult result = await checker.CheckAsync(account, reusePass: false, cancellationToken);
        if (account.ConnectionId is { } connectionId)
        {
            await RecordAsync(connectionId, result, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// For a session under <paramref name="account"/> that exited as a launch failure (the zero-work shape that
    /// raises the node-wide hold): re-checks a bound account without the reuse window, and returns the refusal
    /// when it fails, which the caller holds the run on instead of raising the node-wide hold. Null for the
    /// default login or a passing check, and the caller then does exactly what it did before.
    /// </summary>
    public async Task<ClaudeAccountRefusedException?> RefusalOnLaunchFailureAsync(
        ClaudeCodeAccount account, CancellationToken cancellationToken)
    {
        if (account.ConnectionId is not { } connectionId)
        {
            return null;
        }

        ClaudeAccountCheckResult result = await RecheckAsync(account, cancellationToken);
        return result.Passed
            ? null
            : new ClaudeAccountRefusedException(connectionId, result.Message);
    }

    private Task RecordAsync(Guid connectionId, ClaudeAccountCheckResult result, CancellationToken cancellationToken) =>
        // A reused pass read nothing new, and a refusal clears the reuse entry, so skipping it cannot hide one.
        result.Passed && result.Status is null
            ? Task.CompletedTask
            : ledger.RecordAsync(connectionId, result, cancellationToken);
}

/// <summary>Records refused and cleared on the claude-code connection's own stream, only when the state or the reason changes.</summary>
public sealed class MartenClaudeAccountRefusalLedger(IDocumentStore store, ILogger<MartenClaudeAccountRefusalLedger> logger)
    : IClaudeAccountRefusalLedger
{
    public async Task RecordAsync(Guid connectionId, ClaudeAccountCheckResult result, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ConnectionDetails? connection = await session.LoadAsync<ConnectionDetails>(connectionId, cancellationToken);
        if (connection is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!result.Passed && (!connection.ClaudeCodeRefused || connection.ClaudeCodeRefusedReason != result.Message))
        {
            session.Events.Append(connectionId, new ConnectionClaudeCodeAccountRefused(connectionId, result.Message, now));
            logger.LogWarning(
                "Claude Code account {ConnectionId} refused; nothing starts under it until a check passes: {Reason}",
                connectionId, result.Message);
        }
        else if (result.Passed && connection.ClaudeCodeRefused)
        {
            session.Events.Append(connectionId, new ConnectionClaudeCodeAccountCleared(connectionId, now));
            logger.LogInformation("Claude Code account {ConnectionId} passed its check again; its work resumes", connectionId);
        }
        else
        {
            return;
        }

        await session.SaveChangesAsync(cancellationToken);
    }
}
