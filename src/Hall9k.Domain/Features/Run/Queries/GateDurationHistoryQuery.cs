using Hall9k.Domain.Features.Run.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Run.Queries;

/// <summary>
/// One gate's own comparison against its project's recent history (task: gate wall-clock
/// duration is recorded and surfaced — origin incident 2026-09-01, the full suite roughly
/// doubling in a week and going unnoticed for three days). <see cref="RecentAverage"/> is drawn
/// only from other runs' own recorded <see cref="GateDuration"/> entries for this same gate name
/// — never a fixed or configured number, so the comparison stays honestly "against recent
/// recorded runs" rather than an invented norm.
/// </summary>
public sealed record GateDurationComparison(string Gate, TimeSpan Observed, TimeSpan RecentAverage, int SampleCount);

/// <summary>
/// A project's recently recorded gate durations, loaded once so <c>h9k task show</c> can compare
/// every gate on a run against this same set rather than re-querying per gate (independent pre-PR
/// review, cycle 1 — the two-query lookup used to run once per gate on the newest run).
/// </summary>
public sealed class GateDurationHistory(IReadOnlyList<GateDuration> samples)
{
    /// <summary>How many of the gate's own most recent matching recorded durations count toward the average.</summary>
    private const int MaxSamples = 10;

    /// <summary>
    /// Below this many recorded samples for this gate, there is no honest norm to compare
    /// against — the query says nothing rather than inventing one from too few points.
    /// </summary>
    public const int MinimumSamplesForComparison = 5;

    /// <summary>
    /// How far above the recent average counts as "materially exceeds". The baseline this
    /// compares against is itself a trailing average of the same series (<see cref="MaxSamples"/>
    /// most recent matching samples), so this reliably catches one run's step-change spike
    /// against an otherwise flat recent history, but not a smooth multi-day drift of the kind the
    /// origin incident's own suite showed: a suite growing a few percent per run stays under this
    /// threshold on every single run of that climb, because the baseline climbs right along with
    /// it. Catching that shape needs a human reading the Gates column's own raw numbers over time,
    /// not this flag — this flag is for the sharper, single-run case.
    /// </summary>
    private const double AnomalyMultiplier = 1.5;

    /// <summary>
    /// The comparison for <paramref name="gateName"/> if its <paramref name="observed"/> duration
    /// materially exceeds this project's recent recorded average for that same gate name at the
    /// same <paramref name="ranFullScope"/> classification, and there are enough recorded samples
    /// to say so honestly. Null either way otherwise — too few samples, or a duration that is not
    /// actually anomalous — never a guessed verdict.
    /// </summary>
    public GateDurationComparison? Compare(string gateName, TimeSpan observed, bool ranFullScope)
    {
        TimeSpan[] matching =
        [
            .. samples
                .Where(gate => gate.Gate == gateName && gate.Passed && gate.RanFullScope == ranFullScope)
                .Take(MaxSamples)
                .Select(gate => gate.Duration),
        ];

        if (matching.Length < MinimumSamplesForComparison)
        {
            return null;
        }

        TimeSpan average = TimeSpan.FromTicks((long)matching.Average(duration => duration.Ticks));
        return observed >= average * AnomalyMultiplier
            ? new GateDurationComparison(gateName, observed, average, matching.Length)
            : null;
    }
}

/// <summary>
/// Loads a project's recent gate-duration history for <see cref="GateDurationHistory.Compare"/> —
/// the flag <c>h9k task show</c> renders beside a run's gate durations (task: gate wall-clock
/// duration is recorded and surfaced). Deliberately not a projection of its own: the history is
/// read live from whatever recent runs actually recorded, every time it is asked, so it can never
/// go stale the way a value baked in at record time would as more history accumulates.
/// </summary>
public static class GateDurationHistoryQuery
{
    /// <summary>
    /// How many of the project's most recently DISPATCHED runs to look across for history —
    /// bounded so a large project's lookup stays cheap, generous enough that a project with only
    /// occasional activity still turns up samples. Ordered and bounded on the runs themselves,
    /// not on the tasks that own them (independent pre-PR review, cycle 1): a task can be added
    /// — as a Draft, or as an out-of-scope review finding's own auto-minted bug task — long after
    /// its last dispatched run, and ordering by task recency instead of run recency let enough
    /// undispatched tasks crowd the window that a busy project's own dispatched history fell out
    /// of it entirely.
    /// </summary>
    private const int RecentRunWindow = 50;

    /// <summary>
    /// Every gate duration recorded on this project's <see cref="RecentRunWindow"/> most recently
    /// dispatched runs, excluding <paramref name="excludingRunId"/> so a run's own pass never
    /// counts toward its own baseline. Project membership is checked with a correlated <c>EXISTS</c>
    /// against the task document itself, rather than first materializing every task id the project
    /// has ever had into an in-memory list and shipping it back as an <c>IN</c> parameter
    /// (independent pre-PR review, cycle 2): the run list is already ordered and capped by
    /// <see cref="RunListItem.DispatchedAt"/>, so project membership only needs to be a per-row
    /// filter, not a full enumeration of the project's tasks.
    /// </summary>
    public static async Task<GateDurationHistory> LoadRecentHistoryAsync(
        IQuerySession session, Guid projectId, Guid excludingRunId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(run => run.Id != excludingRunId)
            .Where(run => run.MatchesSql(
                "exists (select 1 from mt_doc_tasklistitem t where t.id = (d.data ->> 'taskId')::uuid and t.data ->> 'projectId' = ?)",
                projectId.ToString()))
            .OrderByDescending(run => run.DispatchedAt)
            .Take(RecentRunWindow)
            .ToListAsync(cancellationToken);

        List<GateDuration> samples = [.. runs.SelectMany(run => run.GateDurations ?? [])];
        return new GateDurationHistory(samples);
    }

    /// <summary>
    /// Convenience single-gate form of <see cref="LoadRecentHistoryAsync"/> plus
    /// <see cref="GateDurationHistory.Compare"/>, for a caller comparing exactly one gate. A
    /// caller comparing every gate on a run should load the history once and call
    /// <see cref="GateDurationHistory.Compare"/> per gate instead, rather than pay this method's
    /// own query once per gate.
    /// </summary>
    public static async Task<GateDurationComparison?> CompareAsync(
        IQuerySession session, Guid projectId, string gateName, TimeSpan observed, bool ranFullScope,
        Guid excludingRunId, CancellationToken cancellationToken)
    {
        GateDurationHistory history = await LoadRecentHistoryAsync(session, projectId, excludingRunId, cancellationToken);
        return history.Compare(gateName, observed, ranFullScope);
    }
}
