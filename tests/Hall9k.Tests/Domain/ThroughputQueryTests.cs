using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Queries;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The pure fold behind the throughput block (task: h9k status reports throughput beside spend):
/// every figure folds <see cref="TaskPassage"/>, the identical record a merged task's own <c>h9k
/// task show</c> passage section renders, so this is exercised straight off hand-built passages
/// rather than a database — the same DB-free split <see cref="ThroughputQuery.Compute"/> and
/// <see cref="TaskPassageQuery.Compute"/> both draw.
/// </summary>
public sealed class ThroughputQueryTests
{
    private static TaskPassage Passage(
        TimeSpan claimToMerge, TimeSpan queued, int reviewCycles, int laps, TimeSpan? humanWait = null) =>
        new(
            Queued: PassagePhase.Closed(queued),
            Building: PassagePhase.NotApplicable,
            Gates: PassagePhase.NotApplicable,
            Review: new ReviewCyclePassage(reviewCycles, TimeSpan.Zero, false, 0, TimeSpan.Zero, false),
            Delivery: PassagePhase.NotApplicable,
            MergeWait: PassagePhase.NotApplicable,
            HumanWaits: humanWait is { } wait
                ? [new HumanWaitPassage(HumanWaitKind.ReviewPark, PassagePhase.Closed(wait))]
                : [],
            ClaimToMerge: PassagePhase.Closed(claimToMerge),
            Laps: laps == 0 ? [] : [new LapKindCount(FollowUpKind.ReviewFeedback, laps)],
            Sessions: 1,
            MergedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Fewer_than_five_merged_reports_only_the_count()
    {
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.Zero, 1, 0),
        ]);

        summary.MergedCount.Should().Be(2);
        summary.HasEnoughData.Should().BeFalse();
        summary.MedianClaimToMerge.Should().BeNull();
        summary.P90ClaimToMerge.Should().BeNull();
        summary.FirstPassMergeShare.Should().BeNull();
        summary.LapsPerMergedTask.Should().BeNull();
        summary.QueuedShare.Should().BeNull();
        summary.HumanWaitShare.Should().BeNull();
    }

    [Fact]
    public void Five_or_more_merged_computes_median_and_p90_claim_to_merge()
    {
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(3), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(4), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(20), TimeSpan.Zero, 1, 0),
        ]);

        summary.HasEnoughData.Should().BeTrue();
        summary.MedianClaimToMerge.Should().Be(TimeSpan.FromHours(3));
        // Linear-interpolation p90 over [1,2,3,4,20]h: rank = 0.9*4 = 3.6 -> between index 3 (4h)
        // and 4 (20h), 0.6 of the way: 4 + 0.6*16 = 13.6h.
        summary.P90ClaimToMerge.Should().Be(TimeSpan.FromHours(13.6));
    }

    [Fact]
    public void First_pass_share_counts_only_tasks_with_no_reopen_and_exactly_one_review_cycle()
    {
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, reviewCycles: 1, laps: 0), // first-pass
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, reviewCycles: 1, laps: 0), // first-pass
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, reviewCycles: 2, laps: 0), // extra cycle, not first-pass
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, reviewCycles: 1, laps: 1), // reopened, not first-pass
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, reviewCycles: 1, laps: 0), // first-pass
        ]);

        summary.FirstPassMergeShare.Should().Be(3.0 / 5);
    }

    [Fact]
    public void Laps_per_merged_task_averages_the_total_lap_count()
    {
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, laps: 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, laps: 2),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, laps: 3),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, laps: 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, laps: 0),
        ]);

        summary.LapsPerMergedTask.Should().Be(1.0);
    }

    [Fact]
    public void Queued_and_human_wait_shares_are_the_period_totals_not_an_average_of_percentages()
    {
        // One long task and four short ones: a share of the whole period's total time weighs the
        // long task's real minutes, rather than letting five equal-weighted percentages average
        // away how much of the total time it actually consumed.
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(20), TimeSpan.FromHours(10), 1, 0, humanWait: TimeSpan.FromHours(4)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
        ]);

        // Total claim-to-merge: 28h. Total queued: 14h. Total human wait: 4h.
        summary.QueuedShare.Should().BeApproximately(14.0 / 28, 0.0001);
        summary.HumanWaitShare.Should().BeApproximately(4.0 / 28, 0.0001);
    }

    [Fact]
    public void A_merged_task_with_unknown_claim_to_merge_is_excluded_from_the_shares_but_still_counted()
    {
        List<TaskPassage> merged =
        [
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0),
            Passage(TimeSpan.FromHours(1), TimeSpan.Zero, 1, 0) with { ClaimToMerge = PassagePhase.Unknown() },
        ];

        ThroughputSummary summary = ThroughputQuery.Compute(merged);

        summary.MergedCount.Should().Be(5);
        summary.HasEnoughData.Should().BeTrue();
        // The unknown task contributes nothing on either side of the ratio, rather than being
        // guessed at zero (AGENTS.md: never guess at unobserved facts).
        summary.QueuedShare.Should().Be(0);
    }
}
