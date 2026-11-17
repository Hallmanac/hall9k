using Hall9k.Connectors.ClaudeCode;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.ClaudeCode;

/// <summary>
/// The CLI's own spawns (<c>h9k task start</c>, <c>h9k task delegate</c>, <c>h9k task drive --direct-launch</c>,
/// <c>h9k orchestrator measure</c>) resolve their account here and check it before they claim, cut a worktree,
/// or append anything, so a refusal exits non-zero with the check's own text and leaves nothing dispatched.
/// </summary>
public static class CliClaudeAccount
{
    /// <summary>The probe every CLI check runs; a test swaps in a fake for the flow it drives.</summary>
    private static readonly AsyncLocal<ClaudeAccountChecker?> CheckerOverride = new();

    private static readonly Lazy<ClaudeAccountChecker> RealChecker = new(() => new ClaudeAccountChecker(new ClaudeAuthStatusProbe()));

    internal static ClaudeAccountChecker Checker => CheckerOverride.Value ?? RealChecker.Value;

    /// <summary>Points this flow's checks at <paramref name="checker"/> until the returned scope is disposed.</summary>
    internal static IDisposable OverrideForTests(ClaudeAccountChecker checker)
    {
        ClaudeAccountChecker? previous = CheckerOverride.Value;
        CheckerOverride.Value = checker;
        return new Restore(() => CheckerOverride.Value = previous);
    }

    /// <summary>The account a run dispatched now for <paramref name="project"/> freezes.</summary>
    public static Task<ClaudeCodeAccount> ForProjectAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken) =>
        ClaudeCodeAccounts.LoadAsync(session, ClaudeCodeAccountBinding.ForProject(project), cancellationToken);

    /// <summary>The account an existing run froze, for a spawn into that run.</summary>
    public static Task<ClaudeCodeAccount> ForRunAsync(
        IQuerySession session, RunDetails run, CancellationToken cancellationToken) =>
        ClaudeCodeAccounts.LoadAsync(session, ClaudeCodeAccountBinding.ForRun(run), cancellationToken);

    /// <summary>Refuses with the check's own text unless <paramref name="account"/> may start a process now.</summary>
    public static async Task EnsureUsableAsync(ClaudeCodeAccount account, CancellationToken cancellationToken)
    {
        ClaudeAccountCheckResult result = await Checker.CheckAsync(account, reusePass: true, cancellationToken);
        if (!result.Passed)
        {
            throw new DomainBusinessRuleException(
                $"Not starting Claude Code: this project runs under the Claude Code account in {account.ConfigDirectory}, "
                + $"and its check failed, so nothing was claimed or started and nothing falls back to the default login. "
                + result.Message);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
