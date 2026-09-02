using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The current period's recorded spend against the node's own budget (backlog: spend-governor
/// step three). <see cref="SpentTokens"/> is always summed live from every recorded
/// <c>TokensRecorded</c> event since the period start, budget set or not, so the calibration loop
/// (run, observe a week's real burn, set the budget under it, adjust) has something to look at
/// from the day this merges rather than the day a number is chosen — that half is current even
/// with no daemon running at all. <see cref="BudgetTokens"/>, <see cref="Period"/> and
/// <see cref="BudgetIsEnforced"/>, though, read the same published <c>NodeDispatchLoad</c> row
/// <see cref="DispatchPressure"/> reads for the concurrency ceiling, freshness-gated the same way:
/// options bind once at daemon startup, so a config-file edit an operator makes without
/// restarting the daemon must not be reported as though it were already in force, and a daemon
/// nobody can currently confirm alive must not be reported as enforcing anything at all
/// (independent pre-PR review, cycle 1, both lenses — the board and the dispatcher must not
/// disagree about whether a budget is even in force). Only <see cref="SummaryLine"/> falls back to
/// the freshly-resolved config for display when no confirmed measurement exists; <see
/// cref="AtBudget"/> never does, since that is the value the Queued section's own gate reads.
/// </summary>
internal sealed record SpendPressure(
    long SpentTokens,
    long? BudgetTokens,
    IReadOnlyList<PeriodSpendByModel> ByModel,
    DateTimeOffset PeriodStart,
    DateTimeOffset NextRollover,
    string Period,
    bool BudgetIsEnforced)
{
    /// <summary>
    /// Whether this period's recorded spend has reached a budget a daemon has confirmed it is
    /// actually enforcing — never a freshly-resolved config value nothing has restarted onto yet
    /// (<see cref="BudgetIsEnforced"/>), so this can never disagree with the dispatcher's own gate
    /// (DispatchEngine.SpendBudgetExhaustedAsync).
    /// </summary>
    public bool AtBudget => BudgetIsEnforced && BudgetTokens is { } budget && SpentTokens >= budget;

    /// <summary>The one-line reason a queued task is not moving, in the same voice <see cref="DispatchPressure.ReasonLine"/> uses for the concurrency ceiling.</summary>
    public string ReasonLine =>
        $"waiting for the spend budget to roll — {SpentTokens:N0} of {BudgetTokens ?? 0:N0} tokens spent this "
        + $"{Period}, rolls {NextRollover:u}";

    /// <summary>The observability line shown regardless of whether a budget is set, confirmed, or neither.</summary>
    public string SummaryLine => (BudgetTokens, BudgetIsEnforced) switch
    {
        ({ } budget, true) => $"spend this {Period}: {SpentTokens:N0} of {budget:N0} tokens (rolls {NextRollover:u})",
        ({ } budget, false) => $"spend this {Period}: {SpentTokens:N0} tokens ({budget:N0}-token budget set but not "
            + $"yet confirmed by a running daemon — h9k daemon start, or restart it, to put it in force; rolls "
            + $"{NextRollover:u})",
        (null, _) => $"spend this {Period}: {SpentTokens:N0} tokens (no budget set; rolls {NextRollover:u})",
    };

    public static async Task<SpendPressure> ReadAsync(
        IQuerySession session, OperatingSettingsReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NodeDispatchLoad? published = await DispatchPressure.ReadFreshMeasurementAsync(session, now, cancellationToken);

        bool budgetIsEnforced = published is { SpendBudgetTokens: not null };
        long? budgetTokens = budgetIsEnforced ? published!.SpendBudgetTokens : report.SpendBudgetTokens.Value;
        string periodValue = published is { SpendPeriod.Length: > 0 } ? published!.SpendPeriod : report.SpendPeriod.Value;

        SpendPeriod period = SpendPeriod.FromInput(periodValue);
        DateTimeOffset periodStart = period.StartOf(now);
        PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
        return new SpendPressure(
            spend.TotalInputTokens, budgetTokens, spend.ByModel, periodStart,
            period.NextRolloverAfter(now), period.Value, budgetIsEnforced);
    }
}
