using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// How fast and how efficiently the platform actually shipped in a period, read beside spend
/// rather than instead of it (task: h9k status reports throughput beside spend, discovery session
/// for idea cc9b7aec — Brian's framing, 2026-09-05: a task moving from claim to merge faster is
/// cost optimized by default, and the one number worth comparing week over week). Every figure
/// here folds <see cref="TaskPassage"/>, the exact record a merged task's own <c>h9k task show</c>
/// passage section renders, so a per-task number and this period's own number can never disagree
/// (the task's own boundary note: "do not build it twice").
/// <para>
/// <see cref="HasEnoughData"/> is the line the reference baseline draws (idea fc85f609's
/// <c>measurement-recipes.md</c>): under five merged tasks, a median is two data points wearing a
/// statistic's clothes, so every derived figure stays null and the caller prints the count with an
/// honest "too few to summarize" instead.
/// </para>
/// </summary>
public sealed record ThroughputSummary(
    int MergedCount,
    TimeSpan? MedianClaimToMerge,
    TimeSpan? P90ClaimToMerge,
    double? FirstPassMergeShare,
    double? LapsPerMergedTask,
    double? QueuedShare,
    double? HumanWaitShare)
{
    /// <summary>Below this, a median or a share is two or three data points dressed as a statistic.</summary>
    public const int MinimumMergedForSummary = 5;

    public bool HasEnoughData => MergedCount >= MinimumMergedForSummary;
}

/// <summary>
/// Computes <see cref="ThroughputSummary"/> for a period by folding <see cref="TaskPassageQuery"/>
/// over every task whose one pull request merged inside it (task: h9k status reports throughput
/// beside spend). Not a projection of its own, for the identical reason
/// <see cref="TaskPassageQuery"/> is not one (that type's own doc): a period's worth of merged
/// tasks is cheap enough to fold whole on every read, and there is no persisted rollup anywhere in
/// this codebase to keep current instead.
/// </summary>
public static class ThroughputQuery
{
    /// <summary>
    /// <paramref name="projectId"/> null reads the whole install (<c>h9k status</c>); set, it scopes
    /// to one project (<c>h9k project show</c>). The candidate set is every <see cref="TaskState.Done"/>
    /// task of any type except <see cref="TaskType.PrReview"/> — that type never watches a merge of
    /// its own (<see cref="TaskPassage.MergedAt"/>'s own doc) — read whole rather than pre-filtered
    /// by a completion timestamp: a task can sit with an open pull request for longer than one
    /// period before it merges, so filtering candidates by when they pushed would silently drop a
    /// task that merged this period after a push from an earlier one. The same trade-off
    /// <see cref="TaskPassageQuery"/>'s own doc already accepts for a single task's replay, taken
    /// once per candidate instead of once per read.
    /// </summary>
    public static async Task<ThroughputSummary> ReadAsync(
        IQuerySession session, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now,
        Guid? projectId, CancellationToken cancellationToken)
    {
        // The state filter runs server-side via MatchesSql, not a plain LINQ `==` against
        // TaskState — the pattern DispatchEngine's own claim query and CloseoutEngine's own
        // missing-run sweep already use for the identical reason: TaskState is a value object
        // with its own JSON converter, not a type Marten's LINQ provider knows how to translate
        // into a JSON-path comparison on its own. Type and the project scope are cheap enough to
        // filter in memory over the (already small) Done candidate set this returns.
        IReadOnlyList<TaskListItem> candidates = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("d.data ->> 'state' = ?", TaskState.Done.Value))
            .ToListAsync(cancellationToken);

        List<TaskPassage> merged = [];
        foreach (TaskListItem candidate in candidates)
        {
            if (candidate.Type == TaskType.PrReview)
            {
                continue;
            }

            if (projectId is { } scopedProjectId && candidate.ProjectId != scopedProjectId)
            {
                continue;
            }

            TaskPassage passage = await TaskPassageQuery.ReadAsync(
                session, candidate.Id, candidate.Type, taskConcluded: true, now, cancellationToken);
            if (passage.MergedAt is { } mergedAt && mergedAt >= periodStart && mergedAt < periodEnd)
            {
                merged.Add(passage);
            }
        }

        return Compute(merged);
    }

    /// <summary>
    /// The pure fold, separated from <see cref="ReadAsync"/>'s own database round trip so it is
    /// unit-testable straight off a list of <see cref="TaskPassage"/> records — the same split
    /// <see cref="TaskPassageQuery.Compute"/> already draws for the identical reason.
    /// </summary>
    public static ThroughputSummary Compute(IReadOnlyList<TaskPassage> merged)
    {
        int count = merged.Count;
        if (count < ThroughputSummary.MinimumMergedForSummary)
        {
            return new ThroughputSummary(count, null, null, null, null, null, null);
        }

        List<TimeSpan> claimToMerge = [.. merged
            .Select(passage => passage.ClaimToMerge.Elapsed)
            .OfType<TimeSpan>()
            .Order()];

        TimeSpan? median = claimToMerge.Count > 0 ? Percentile(claimToMerge, 0.5) : null;
        TimeSpan? p90 = claimToMerge.Count > 0 ? Percentile(claimToMerge, 0.9) : null;

        // First-pass: merged with no reopen at all (every kind of TaskReopened lap counts against
        // it, not only a review-feedback one) and exactly the one review cycle an opening pass
        // that needed no fix session ever dispatches.
        int firstPass = merged.Count(passage =>
            passage.Laps.Sum(lap => lap.Count) == 0 && passage.Review.Cycles == 1);
        double firstPassShare = (double)firstPass / count;

        double lapsPerMergedTask = merged.Average(passage => passage.Laps.Sum(lap => lap.Count));

        double? queuedShare = ShareOfClaimToMerge(merged, passage => passage.Queued.Elapsed);
        double? humanWaitShare = ShareOfClaimToMerge(
            merged,
            passage => passage.HumanWaits.Aggregate(
                TimeSpan.Zero, (sum, wait) => sum + (wait.Elapsed.Elapsed ?? TimeSpan.Zero)));

        return new ThroughputSummary(count, median, p90, firstPassShare, lapsPerMergedTask, queuedShare, humanWaitShare);
    }

    /// <summary>
    /// One phase's share of the total claim-to-merge time across every merged task that carries a
    /// known figure for both — the reference baseline's own shape (idea fc85f609: "26 percent
    /// before the first pull request... 14 percent waiting for the merge"), a share of the whole
    /// period's total rather than an average of each task's own percentage, so one long-running
    /// task cannot swing the figure as hard as the total time it actually spent. A task whose own
    /// claim-to-merge is unknown (an older stream missing a boundary) contributes to neither side
    /// rather than being guessed at zero.
    /// </summary>
    private static double? ShareOfClaimToMerge(
        IReadOnlyList<TaskPassage> merged, Func<TaskPassage, TimeSpan?> numerator)
    {
        TimeSpan numeratorTotal = TimeSpan.Zero;
        TimeSpan denominatorTotal = TimeSpan.Zero;
        foreach (TaskPassage passage in merged)
        {
            if (passage.ClaimToMerge.Elapsed is not { } claimToMerge)
            {
                continue;
            }

            denominatorTotal += claimToMerge;
            numeratorTotal += numerator(passage) ?? TimeSpan.Zero;
        }

        return denominatorTotal > TimeSpan.Zero ? numeratorTotal / denominatorTotal : null;
    }

    /// <summary>Linear-interpolation percentile (Excel's PERCENTILE.INC) over an ascending list.</summary>
    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> sortedAscending, double p)
    {
        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        double rank = p * (sortedAscending.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedAscending[lower];
        }

        double fraction = rank - lower;
        return sortedAscending[lower] + ((sortedAscending[upper] - sortedAscending[lower]) * fraction);
    }
}
