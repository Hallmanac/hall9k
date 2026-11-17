using System.Collections.Concurrent;
using Hall9k.Domain.Features.Connection;

namespace Hall9k.Connectors.ClaudeCode;

/// <summary>
/// The pre-spawn check for a bound account: the directory exists, then <c>claude auth status</c>, run with the
/// binary and the environment the spawn gets, reports logged in as the recorded email and org id. The default
/// login is never checked. A pass may be reused for the same directory and account for
/// <see cref="ReuseWindow"/>; a failure never is, so a fixed login is seen on the next check.
/// </summary>
public sealed class ClaudeAccountChecker(
    IClaudeAuthStatusProbe probe,
    TimeProvider? time = null,
    Func<string, bool>? directoryExists = null,
    bool? windows = null)
{
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;
    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();
    private readonly ConcurrentDictionary<(string Directory, string? Email, string? OrganizationId), DateTimeOffset> _passes = new();

    /// <summary>
    /// Checks <paramref name="account"/>. Completes synchronously for the default login and for a reused pass.
    /// <paramref name="reusePass"/> false ignores a recent pass, for a re-check that must see the login as it is now.
    /// </summary>
    public ValueTask<ClaudeAccountCheckResult> CheckAsync(
        ClaudeCodeAccount account, bool reusePass, CancellationToken cancellationToken)
    {
        if (account.ConfigDirectory is not { } directory)
        {
            return ValueTask.FromResult(new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.Passed, null, "The default Claude Code login is not checked."));
        }

        var key = (directory, account.Email, account.OrganizationId);
        return reusePass && _passes.TryGetValue(key, out DateTimeOffset passedAt) && _time.GetUtcNow() - passedAt < ReuseWindow
            ? ValueTask.FromResult(new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.Passed, null, $"{directory} passed its check at {passedAt:u}; reused."))
            : new ValueTask<ClaudeAccountCheckResult>(ProbeAsync(key, cancellationToken));
    }

    /// <summary>Checks a directory being registered: any account that names itself passes, and nothing is reused.</summary>
    public async Task<ClaudeAccountCheckResult> CheckNewDirectoryAsync(string directory, CancellationToken cancellationToken) =>
        !_directoryExists(directory)
            ? ClaudeAccountCheck.Missing(directory, _windows)
            : ClaudeAccountCheck.Evaluate(
                directory, await probe.ReadAsync(directory, cancellationToken), null, null, _windows);

    private async Task<ClaudeAccountCheckResult> ProbeAsync(
        (string Directory, string? Email, string? OrganizationId) key, CancellationToken cancellationToken)
    {
        _passes.TryRemove(key, out _);
        if (!_directoryExists(key.Directory))
        {
            return ClaudeAccountCheck.Missing(key.Directory, _windows);
        }

        // A connection whose account was never observed has nothing to match against; refused like a wrong
        // account rather than passed on whatever the directory happens to hold.
        ClaudeAccountCheckResult result = ClaudeAccountCheck.Evaluate(
            key.Directory, await probe.ReadAsync(key.Directory, cancellationToken),
            key.Email ?? string.Empty, key.OrganizationId, _windows);
        if (result.Passed)
        {
            _passes[key] = _time.GetUtcNow();
        }

        return result;
    }
}
