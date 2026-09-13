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
    /// <summary>
    /// <paramref name="queuedBeforeFirstClaim"/> defaults to <paramref name="queued"/> — the
    /// ordinary shape for a task claimed once and never reopened, where the lifetime queued total
    /// and the leading-edge-alone total are the identical figure. A test exercising a task that
    /// requeued again after a reopen (so its lifetime <paramref name="queued"/> exceeds the portion
    /// that occurred before its first claim) passes the two separately.
    /// </summary>
    private static TaskPassage Passage(
        TimeSpan claimToMerge, TimeSpan queued, int reviewCycles, int laps, TimeSpan? humanWait = null,
        TimeSpan? queuedBeforeFirstClaim = null) =>
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
            MergedAt: DateTimeOffset.UtcNow,
            QueuedBeforeFirstClaim: PassagePhase.Closed(queuedBeforeFirstClaim ?? queued));

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
        // away how much of the total time it actually consumed. Each task's own queued time falls
        // entirely before its first claim (the ordinary shape, no reopen), so the denominator here
        // is claim-to-merge plus that leading queued time, not claim-to-merge alone.
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(20), TimeSpan.FromHours(10), 1, 0, humanWait: TimeSpan.FromHours(4)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
        ]);

        // Total claim-to-merge: 28h. Total queued-before-first-claim: 14h. Total lifetime: 42h.
        // Total human wait: 4h.
        summary.QueuedShare.Should().BeApproximately(14.0 / 42, 0.0001);
        summary.HumanWaitShare.Should().BeApproximately(4.0 / 42, 0.0001);
    }

    [Fact]
    public void Queued_share_never_exceeds_one_even_when_every_task_queued_longer_than_it_took_to_ship()
    {
        // The exact shape an independent pre-PR review found reading "queued 500% of task time":
        // five tasks that each waited 10h behind the ceiling before their first claim, then took
        // only 2h from claim to merge. Dividing queued time by claim-to-merge alone put the
        // pre-claim wait in the numerator with no matching half in the denominator; the fix widens
        // the denominator to the task's whole life (queued-before-first-claim plus claim-to-merge),
        // which the numerator is always a subset of.
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(10), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(10), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(10), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(10), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(10), 1, 0),
        ]);

        summary.QueuedShare.Should().BeApproximately(10.0 / 12, 0.0001);
        summary.QueuedShare.Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void Queued_share_counts_a_later_requeue_without_double_counting_against_the_leading_wait()
    {
        // A task reopened and requeued after its first claim: its lifetime queued total (5h) is
        // more than the queued time before its first claim alone (2h) — the extra 3h happened
        // during the claim-to-merge window (a review-feedback lap), which claim-to-merge's own 10h
        // span already accounts for as wall-clock time. The denominator is still just
        // queued-before-first-claim plus claim-to-merge (12h), not the lifetime queued total added
        // on top a second time, so the numerator (the full 5h lifetime total) stays a subset of it.
        ThroughputSummary summary = ThroughputQuery.Compute(
        [
            Passage(TimeSpan.FromHours(10), TimeSpan.FromHours(5), 1, 1, queuedBeforeFirstClaim: TimeSpan.FromHours(2)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0),
        ]);

        // Denominator: (10h+2h) + 4*(2h+1h) = 12h + 12h = 24h. Numerator: 5h + 4*1h = 9h.
        summary.QueuedShare.Should().BeApproximately(9.0 / 24, 0.0001);
        summary.QueuedShare.Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void A_human_wait_that_never_closed_excludes_the_task_from_the_human_wait_share_rather_than_reading_as_zero()
    {
        // AGENTS.md's never-guess rule: a human wait this task's own stream never recorded a close
        // for must not be folded in as though it never happened at all.
        List<TaskPassage> merged =
        [
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0, humanWait: TimeSpan.FromHours(1)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0, humanWait: TimeSpan.FromHours(1)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0, humanWait: TimeSpan.FromHours(1)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0, humanWait: TimeSpan.FromHours(1)),
            Passage(TimeSpan.FromHours(2), TimeSpan.FromHours(1), 1, 0) with
            {
                HumanWaits = [new HumanWaitPassage(HumanWaitKind.ReviewPark, PassagePhase.Unknown())],
            },
        ];

        ThroughputSummary summary = ThroughputQuery.Compute(merged);

        // The unknown-wait task contributes to neither side: 4 * (1h / 3h) = 4h / 12h.
        summary.HumanWaitShare.Should().BeApproximately(4.0 / 12, 0.0001);
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
