namespace Hall9k.Daemon.Execution;

/// <summary>
/// Tells "the node never actually launched a working session" apart from an ordinary agent
/// failure (task: a session that exits at once with no work done is treated as the node failing
/// to launch sessions) — on the zero-work SHAPE the 2026-09-07 outage's sessions all shared (one
/// turn, zero tokens, 85 to 208 ms), never on message text: that same window also held a GitHub
/// fetch timeout beside the expired-credential message, and the next cause will be a different
/// string again. The message is reported to the human (<c>DaemonOptions.LaunchFailureMaxDuration</c>'s
/// own doc), never matched.
/// <para>
/// Checked only once <see cref="BudgetExhaustionParser.IsBudgetExhausted"/> has already said no:
/// a usage-limit result can itself arrive with no measurable output on a session that spent
/// several real turns getting there, and the budget shape is the one this platform already
/// recognizes by text on purpose — this classifier must never re-catch it under a different
/// label (deliberately narrow, the same discipline <see cref="BudgetExhaustionParser"/>'s own doc
/// states).
/// </para>
/// </summary>
public static class LaunchFailureClassifier
{
    public static bool IsLaunchFailure(AgentResult result, TimeSpan maxDuration) =>
        result.IsError
        && result.Turns == 1
        && result.TotalInputTokens == 0
        && result.OutputTokens == 0
        && result.DurationMs is { } durationMs
        && durationMs < maxDuration.TotalMilliseconds;
}
