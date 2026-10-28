using Hall9k.Domain.Features.Owner;
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
    double? HumanWaitShare,
    PhaseClassShares? PhaseShares = null,
    IReadOnlyList<OwnerPhaseShares>? OwnerShares = null)
{
    /// <summary>Below this, a median or a share is two or three data points dressed as a statistic.</summary>
    public const int MinimumMergedForSummary = 5;

    public bool HasEnoughData => MergedCount >= MinimumMergedForSummary;
}

/// <summary>
/// The share of a period's phases that each class ran (idea 6e6d0200, Card 6): the period's own total
/// phase count is the denominator, never an average of each task's own percentage, so one long task
/// weighs what its phases weigh. The four shares sum to 1.
/// </summary>
public sealed record PhaseClassShares(double Human, double Delegated, double Automatic, double Skipped);

/// <summary>
/// One owner's slice of the period's phases, keyed by the owner who held the claim each phase ran
/// under (<see cref="PhaseOwnerKey"/>). <see cref="MergedCount"/> counts the merged tasks with at least
/// one phase keyed to the owner, and <see cref="Shares"/> is null under the same five-task floor the
/// period as a whole answers to, so a slice of two tasks is never printed as a statistic.
/// </summary>
public sealed record OwnerPhaseShares(string OwnerKey, int MergedCount, PhaseClassShares? Shares)
{
    public bool HasEnoughData => MergedCount >= ThroughputSummary.MinimumMergedForSummary;
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

        return Compute(merged, await ResolveOwnerFingerprintsAsync(session, merged, cancellationToken));
    }

    /// <summary>
    /// The root fingerprint of every owner a phase ran under whose claim carried none (every claim
    /// written before that field), resolved from the owner's own Guid so one person's several nodes
    /// still count as one owner. An owner who has claimed no root yet resolves to nothing and keys on
    /// the Guid instead.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> ResolveOwnerFingerprintsAsync(
        IQuerySession session, IReadOnlyList<TaskPassage> merged, CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> resolved = [];
        IEnumerable<Guid> unresolved = merged
            .SelectMany(passage => passage.Classification?.Phases ?? [])
            .Select(phase => phase.Owner)
            .OfType<ClaimOwner>()
            .Where(owner => string.IsNullOrWhiteSpace(owner.RootFingerprint))
            .Select(owner => owner.OwnerId)
            .Distinct();
        foreach (Guid ownerId in unresolved)
        {
            if (await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken) is { Length: > 0 } fingerprint)
            {
                resolved[ownerId] = fingerprint;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The pure fold, separated from <see cref="ReadAsync"/>'s own database round trip so it is
    /// unit-testable straight off a list of <see cref="TaskPassage"/> records — the same split
    /// <see cref="TaskPassageQuery.Compute"/> already draws for the identical reason.
    /// </summary>
    public static ThroughputSummary Compute(
        IReadOnlyList<TaskPassage> merged, IReadOnlyDictionary<Guid, string>? resolvedFingerprints = null)
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

        (PhaseClassShares? phaseShares, IReadOnlyList<OwnerPhaseShares> ownerShares) =
            FoldPhaseShares(merged, resolvedFingerprints ?? new Dictionary<Guid, string>());

        return new ThroughputSummary(
            count, median, p90, firstPassShare, lapsPerMergedTask, queuedShare, humanWaitShare, phaseShares, ownerShares);
    }

    /// <summary>
    /// The share of phases in each class over every merged task's classification (period totals, not
    /// an average of per-task percentages), and the same split per owner. An owner is the one whose
    /// claim a phase ran under, by root fingerprint (<see cref="ClaimOwner.RootFingerprint"/>, else the
    /// one <paramref name="resolvedFingerprints"/> resolves from the owner's Guid). A task counts for an
    /// owner when at least one of its phases is keyed to them, and a slice under the five-task floor
    /// keeps its count but carries no shares.
    /// </summary>
    private static (PhaseClassShares? Overall, IReadOnlyList<OwnerPhaseShares> Owners) FoldPhaseShares(
        IReadOnlyList<TaskPassage> merged, IReadOnlyDictionary<Guid, string> resolvedFingerprints)
    {
        int[] overall = new int[4];
        Dictionary<string, int[]> byOwner = [];
        Dictionary<string, HashSet<int>> tasksByOwner = [];
        for (int taskIndex = 0; taskIndex < merged.Count; taskIndex++)
        {
            foreach (ClassifiedPhase phase in merged[taskIndex].Classification?.Phases ?? [])
            {
                int slot = (int)phase.Actor;
                overall[slot]++;
                if (phase.Owner is not { } owner)
                {
                    continue;
                }

                string key = PhaseOwnerKey.Of(
                    string.IsNullOrWhiteSpace(owner.RootFingerprint)
                        ? resolvedFingerprints.GetValueOrDefault(owner.OwnerId)
                        : owner.RootFingerprint,
                    owner.OwnerId);
                if (!byOwner.TryGetValue(key, out int[]? counts))
                {
                    counts = new int[4];
                    byOwner[key] = counts;
                    tasksByOwner[key] = [];
                }

                counts[slot]++;
                tasksByOwner[key].Add(taskIndex);
            }
        }

        List<OwnerPhaseShares> owners = [.. byOwner
            .Select(entry => new OwnerPhaseShares(
                entry.Key,
                tasksByOwner[entry.Key].Count,
                tasksByOwner[entry.Key].Count >= ThroughputSummary.MinimumMergedForSummary ? SharesOf(entry.Value) : null))
            .OrderByDescending(owner => owner.MergedCount)
            .ThenBy(owner => owner.OwnerKey, StringComparer.Ordinal)];

        return (SharesOf(overall), owners);
    }

    /// <summary>Counts indexed by <see cref="PhaseActor"/> as shares of their own total; null when there are no phases to share.</summary>
    private static PhaseClassShares? SharesOf(int[] counts)
    {
        int total = counts.Sum();
        return total == 0
            ? null
            : new PhaseClassShares(
                (double)counts[(int)PhaseActor.Human] / total,
                (double)counts[(int)PhaseActor.Delegated] / total,
                (double)counts[(int)PhaseActor.Automatic] / total,
                (double)counts[(int)PhaseActor.Skipped] / total);
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
