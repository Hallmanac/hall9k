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
    DateTimeOffset NextRollover,
    string Period,
    bool BudgetIsEnforced,
    long? ConfiguredBudgetTokens,
    string ConfiguredPeriod)
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

    /// <summary>
    /// One line per model with recorded spend this period, in the shared format <c>h9k status</c>
    /// and <c>h9k config show</c> both print beneath <see cref="SummaryLine"/> — kept here so the
    /// two surfaces cannot drift into two different renderings of the same figure.
    /// </summary>
    public IReadOnlyList<string> ByModelLines =>
        [.. ByModel.Select(entry =>
            $"  {(entry.Model == AgentModel.Unknown ? "(unknown model)" : entry.Model.Value)}: {entry.TotalInputTokens:N0} tokens")];

    /// <summary>The observability line shown regardless of whether a budget is set, confirmed, or neither.</summary>
    public string SummaryLine => (BudgetTokens, BudgetIsEnforced) switch
    {
        ({ } budget, true) => $"spend this {Period}: {SpentTokens:N0} of {budget:N0} tokens (rolls {NextRollover:u})"
            + PendingChangeNote(budget, Period),
        ({ } budget, false) => $"spend this {Period}: {SpentTokens:N0} tokens ({budget:N0}-token budget set but not "
            + $"yet confirmed by a running daemon — h9k daemon start, or restart it, to put it in force; rolls "
            + $"{NextRollover:u})",
        (null, _) => $"spend this {Period}: {SpentTokens:N0} tokens (no budget set; rolls {NextRollover:u})",
    };

    /// <summary>
    /// A change to an already-enforced budget or period is a second transition <see
    /// cref="SummaryLine"/>'s own set-but-not-yet-confirmed branch does not cover, because that
    /// branch only fires when <see cref="BudgetIsEnforced"/> is false — here it is still true, on
    /// the daemon's old value, while this shell resolves a different one, in either the budget or
    /// the period (or both). Without this note the two figures sit side by side on
    /// <c>h9k config show</c> (this line, and
    /// <c>OperatingSettingsRendering.DescribeSpendBudgetTokens</c>'s row) with nothing saying which
    /// one is actually in force. Fires on a resolved <c>null</c> budget too — clearing an
    /// already-enforced budget (<c>h9k config set --spend-budget none</c>) is the same
    /// disagreement, not a case the prior non-null-only check should have let through. The wording
    /// does not assume a pending file edit is the cause: the daemon's own environment can carry
    /// <c>Hall9k__SpendBudgetTokens</c> while this shell's does not, in which case "restart the
    /// daemon" is the wrong remedy, so the note names the disagreement rather than a diagnosis.
    /// </summary>
    private string PendingChangeNote(long enforcedBudget, string enforcedPeriod) =>
        ConfiguredBudgetTokens != enforcedBudget || ConfiguredPeriod != enforcedPeriod
            ? $" — this shell resolves "
                + $"{(ConfiguredBudgetTokens is { } configured ? $"{configured:N0}" : "no budget")} "
                + $"per {ConfiguredPeriod}, which differs from what the daemon is enforcing; restart the daemon if "
                + "a config change should take effect, or check whether it sees a different environment"
            : string.Empty;

    public static async Task<SpendPressure> ReadAsync(
        IQuerySession session, OperatingSettingsReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NodeDispatchLoad? published = await DispatchPressure.ReadFreshMeasurementAsync(session, now, cancellationToken);

        // Period rides the same enforcement gate the budget does: a published row always carries
        // some SpendPeriod (the daemon's compiled default when no budget is set), so trusting it
        // whenever it is merely non-empty would report a window the daemon isn't actually
        // enforcing (independent pre-PR review, cycle 7, adversarial lens).
        bool budgetIsEnforced = published is { SpendBudgetTokens: not null };
        long? budgetTokens = budgetIsEnforced ? published!.SpendBudgetTokens : report.SpendBudgetTokens.Value;
        string periodValue = budgetIsEnforced ? published!.SpendPeriod : report.SpendPeriod.Value;

        SpendPeriod period = SpendPeriod.FromInput(periodValue);
        DateTimeOffset periodStart = period.StartOf(now);
        PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
        return new SpendPressure(
            spend.TotalInputTokens, budgetTokens, spend.ByModel,
            period.NextRolloverAfter(now), period.Value, budgetIsEnforced, report.SpendBudgetTokens.Value,
            report.SpendPeriod.Value);
    }
}
