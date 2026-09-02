using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

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
/// Whether a gate's own duration this pass materially exceeds what the same gate has recently
/// cost, elsewhere in this project — the flag <c>h9k task show</c> renders beside a run's gate
/// durations (task: gate wall-clock duration is recorded and surfaced). Deliberately not a
/// projection of its own: the comparison is read live from whatever recent runs actually
/// recorded, every time it is asked, so it can never go stale the way a value baked in at record
/// time would as more history accumulates.
/// </summary>
public static class GateDurationHistoryQuery
{
    /// <summary>
    /// How many of the project's most recently dispatched tasks to look across for history —
    /// bounded so a large project's lookup stays cheap, generous enough that a project with only
    /// occasional activity still turns up samples.
    /// </summary>
    private const int RecentTaskWindow = 50;

    /// <summary>How many of the gate's own most recent recorded durations count toward the average.</summary>
    private const int MaxSamples = 10;

    /// <summary>
    /// Below this many recorded samples for this gate, there is no honest norm to compare
    /// against — the query says nothing rather than inventing one from too few points.
    /// </summary>
    public const int MinimumSamplesForComparison = 5;

    /// <summary>
    /// How far above the recent average counts as "materially exceeds" — the origin incident's
    /// own suite roughly doubled before anyone noticed; this catches drift well before a full
    /// doubling rather than waiting for one.
    /// </summary>
    private const double AnomalyMultiplier = 1.5;

    /// <summary>
    /// The comparison for <paramref name="gateName"/> if its <paramref name="observed"/> duration
    /// materially exceeds this project's recent recorded average for that same gate name, and
    /// there are enough recorded samples to say so honestly. Null either way otherwise — too few
    /// samples, or a duration that is not actually anomalous — never a guessed verdict.
    /// </summary>
    public static async Task<GateDurationComparison?> CompareAsync(
        IQuerySession session, Guid projectId, string gateName, TimeSpan observed, Guid excludingRunId,
        CancellationToken cancellationToken)
    {
        (TimeSpan average, int sampleCount) =
            await RecentAverageAsync(session, projectId, gateName, excludingRunId, cancellationToken);
        if (sampleCount < MinimumSamplesForComparison)
        {
            return null;
        }

        return observed >= average * AnomalyMultiplier
            ? new GateDurationComparison(gateName, observed, average, sampleCount)
            : null;
    }

    private static async Task<(TimeSpan Average, int SampleCount)> RecentAverageAsync(
        IQuerySession session, Guid projectId, string gateName, Guid excludingRunId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> taskIds = await session.Query<TaskListItem>()
            .Where(task => task.ProjectId == projectId)
            .OrderByDescending(task => task.AddedAt)
            .Take(RecentTaskWindow)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        if (taskIds.Count == 0)
        {
            return (TimeSpan.Zero, 0);
        }

        Guid[] wanted = [.. taskIds];
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(run => run.TaskId.IsOneOf(wanted))
            .OrderByDescending(run => run.DispatchedAt)
            .ToListAsync(cancellationToken);

        TimeSpan[] samples =
        [
            .. runs
                .Where(run => run.Id != excludingRunId && run.GateDurations is not null)
                .SelectMany(run => run.GateDurations!)
                .Where(gate => gate.Gate == gateName)
                .Select(gate => gate.Duration)
                .Take(MaxSamples),
        ];

        return samples.Length == 0
            ? (TimeSpan.Zero, 0)
            : (TimeSpan.FromTicks((long)samples.Average(duration => duration.Ticks)), samples.Length);
    }
}
