using Hall9k.Domain.Features.Run.Projections;
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
    /// its own (<see cref="TaskPassage.MergedAt"/>'s own doc) — narrowed first to tasks with at
    /// least one run that finished on or after <paramref name="periodStart"/>
    /// (<see cref="RunListItem.FinishedAt"/>, set by <c>RunCompleted</c> only once a merge has been
    /// observed) before replaying any of them, rather than replaying every Done task the install
    /// has ever had: a merge inside the window can only have been observed at or after
    /// <see cref="RunListItem.FinishedAt"/>, which is itself never earlier than
    /// <c>PullRequestMerged</c> (that field's own doc), so this pre-filter can only ever exclude a
    /// task whose merge is provably outside the window, never one inside it. Unlike filtering by
    /// when a task pushed — which <see cref="TaskPassageQuery"/>'s own doc already rejects, since a
    /// pull request can sit open across a period boundary before it merges — filtering by the
    /// merge's own observation loses nothing (independent pre-PR review, cycle 1, both lenses: an
    /// unfiltered replay of every Done task the install has ever had made every <c>h9k status</c>
    /// call, and each of the two <c>h9k project show</c> makes, grow without bound as the install's
    /// history grew).
    /// </summary>
    public static async Task<ThroughputSummary> ReadAsync(
        IQuerySession session, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now,
        Guid? projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> recentlyFinishedTaskIds = await session.Query<RunListItem>()
            .Where(run => run.FinishedAt != null && run.FinishedAt >= periodStart)
            .Select(run => run.TaskId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // The state filter runs server-side via MatchesSql, not a plain LINQ `==` against
        // TaskState — the pattern DispatchEngine's own claim query and CloseoutEngine's own
        // missing-run sweep already use for the identical reason: TaskState is a value object
        // with its own JSON converter, not a type Marten's LINQ provider knows how to translate
        // into a JSON-path comparison on its own. Type and the project scope are cheap enough to
        // filter in memory over the (already narrowed) Done candidate set this returns.
        IReadOnlyList<TaskListItem> candidates = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("d.data ->> 'state' = ?", TaskState.Done.Value))
            .Where(task => recentlyFinishedTaskIds.Contains(task.Id))
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

        double? queuedShare = ShareOfTaskLifetime(merged, passage => passage.Queued.Elapsed);
        double? humanWaitShare = ShareOfTaskLifetime(
            merged,
            passage => passage.HumanWaits.Any(wait => wait.Elapsed.IsUnknown)
                ? null
                : passage.HumanWaits.Aggregate(
                    TimeSpan.Zero, (sum, wait) => sum + (wait.Elapsed.Elapsed ?? TimeSpan.Zero)));

        return new ThroughputSummary(count, median, p90, firstPassShare, lapsPerMergedTask, queuedShare, humanWaitShare);
    }

    /// <summary>
    /// One phase's share of the total task lifetime — first queue entry to merge — across every
    /// merged task that carries a known figure for both (the reference baseline's own shape, idea
    /// fc85f609: "26 percent before the first pull request... 14 percent waiting for the merge"), a
    /// share of the whole period's total rather than an average of each task's own percentage, so
    /// one long-running task cannot swing the figure as hard as the total time it actually spent.
    /// <para>
    /// The denominator is <see cref="TaskPassage.QueuedBeforeFirstClaim"/> plus
    /// <see cref="TaskPassage.ClaimToMerge"/>, not <see cref="TaskPassage.ClaimToMerge"/> alone: a
    /// task's queued time before its very first claim falls entirely outside the claim-to-merge
    /// window, so dividing by that window alone could read over 100 percent queued whenever a task
    /// ever waited before its first claim (independent pre-PR review, cycle 1, both lenses — five
    /// tasks queued 10h then claimed-to-merged in 2h each read "queued 500%"). The two phases never
    /// overlap and together span this task's whole life from its first queue entry to its merge, so
    /// every phase this method is ever asked to share against that whole — the numerator here,
    /// which spans the same window by construction — is bounded by it.
    /// </para>
    /// <para>
    /// A task missing either half of the denominator, or whose own numerator reads unknown rather
    /// than a real duration, contributes to neither side rather than being guessed at zero
    /// (AGENTS.md: never guess at unobserved facts) — an older stream missing a boundary, or a
    /// human wait this task's own stream never recorded a close for.
    /// </para>
    /// </summary>
    private static double? ShareOfTaskLifetime(
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

            if (passage.QueuedBeforeFirstClaim.Elapsed is not { } queuedBeforeFirstClaim)
            {
                continue;
            }

            if (numerator(passage) is not { } numeratorValue)
            {
                continue;
            }

            denominatorTotal += claimToMerge + queuedBeforeFirstClaim;
            numeratorTotal += numeratorValue;
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
