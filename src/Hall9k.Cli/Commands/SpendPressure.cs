using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The current period's recorded spend against the node's own budget (backlog: spend-governor
/// step three), read live the same way <see cref="DispatchPressure"/> reads the concurrency
/// ceiling — but always computed, budget set or not, so the calibration loop (run, observe a
/// week's real burn, set the budget under it, adjust) has something to look at from the day this
/// merges rather than the day a number is chosen. Unlike <see cref="DispatchPressure"/>, this is
/// not published telemetry from a daemon sweep: it is summed live from every recorded
/// <c>TokensRecorded</c> event since the period start, so it is current even with no daemon
/// running at all.
/// </summary>
internal sealed record SpendPressure(
    long SpentTokens,
    long? BudgetTokens,
    IReadOnlyList<PeriodSpendByModel> ByModel,
    DateTimeOffset PeriodStart,
    DateTimeOffset NextRollover,
    string Period)
{
    /// <summary>Whether this period's recorded spend has reached the budget — the dispatcher's own gate (DispatchEngine.SpendBudgetExhaustedAsync).</summary>
    public bool AtBudget => BudgetTokens is { } budget && SpentTokens >= budget;

    /// <summary>The one-line reason a queued task is not moving, in the same voice <see cref="DispatchPressure.ReasonLine"/> uses for the concurrency ceiling.</summary>
    public string ReasonLine =>
        $"waiting for the spend budget to roll — {SpentTokens:N0} of {BudgetTokens ?? 0:N0} tokens spent this "
        + $"{Period}, rolls {NextRollover:u}";

    /// <summary>The observability line shown regardless of whether a budget is set at all.</summary>
    public string SummaryLine => BudgetTokens is { } budget
        ? $"spend this {Period}: {SpentTokens:N0} of {budget:N0} tokens (rolls {NextRollover:u})"
        : $"spend this {Period}: {SpentTokens:N0} tokens (no budget set; rolls {NextRollover:u})";

    public static async Task<SpendPressure> ReadAsync(
        IQuerySession session, OperatingSettingsReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SpendPeriod period = SpendPeriod.FromInput(report.SpendPeriod.Value);
        DateTimeOffset periodStart = period.StartOf(now);
        PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
        return new SpendPressure(
            spend.TotalInputTokens, report.SpendBudgetTokens.Value, spend.ByModel, periodStart,
            period.NextRolloverAfter(now), period.Value);
    }
}
