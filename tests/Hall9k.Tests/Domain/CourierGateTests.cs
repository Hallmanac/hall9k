using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The feed courier's own spawn decision (idea 89471598, piece 3), driven purely through
/// <see cref="CourierGate"/> with hand-supplied facts — nothing here touches a database, a
/// process, or an agent. Covers each of the four acceptance-criteria conditions in turn, the
/// batching wait's own ramp, the urgent override, and the per-day spawn cap.
/// </summary>
public sealed class CourierGateTests
{
    private static readonly TimeSpan QuietThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.Parse("0197a000-0000-7000-8000-000000000001");

    private static CourierSpawnDecision Decide(
        bool hasUndrainedItems = true,
        bool orchestratorLive = true,
        bool courierAlreadyRunning = false,
        bool manualDrainLeaseHeld = false,
        bool hasUrgentItem = false,
        long urgentSequence = 100,
        CourierFailureStreak? streak = null,
        TimeSpan? elapsedSinceLastCourier = null,
        TimeSpan? quietFor = null,
        TimeSpan? maxWait = null,
        int spawnsToday = 0,
        int daySpawnCap = 500) =>
        CourierGate.Decide(
            hasUndrainedItems, orchestratorLive, courierAlreadyRunning, manualDrainLeaseHeld,
            hasUrgentItem ? urgentSequence : null, streak, Now, elapsedSinceLastCourier, quietFor ?? TimeSpan.Zero,
            QuietThreshold, maxWait ?? MaxWait, spawnsToday, daySpawnCap);

    /// <summary>
    /// A streak of <paramref name="count"/> failures whose last one completed <paramref name="agoOfLastFailure"/>
    /// before <see cref="Now"/>, with the next attempt where the defaults put it.
    /// </summary>
    private static CourierFailureStreak Streak(int count, TimeSpan agoOfLastFailure, long carried = 50)
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30));
        DateTimeOffset lastFailureAt = Now - agoOfLastFailure;
        return new CourierFailureStreak
        {
            Id = ProjectId,
            Count = count,
            StartedAt = lastFailureAt - TimeSpan.FromHours(1),
            LastFailureAt = lastFailureAt,
            LastFailureReason = "not reachable",
            HighestCarriedSequence = carried,
            NextAttemptAt = lastFailureAt + waits.Step(count),
        };
    }

    [Fact]
    public void No_undrained_items_refuses_regardless_of_everything_else()
    {
        Decide(hasUndrainedItems: false, orchestratorLive: false, hasUrgentItem: true, spawnsToday: 1000)
            .Should().Be(CourierSpawnDecision.NoItems);
    }

    [Fact]
    public void No_live_orchestrator_refuses_even_with_items_and_no_wait_outstanding()
    {
        Decide(orchestratorLive: false, elapsedSinceLastCourier: null).Should().Be(CourierSpawnDecision.NoOrchestrator);
    }

    [Fact]
    public void A_courier_already_running_for_the_project_refuses_a_second_one()
    {
        Decide(courierAlreadyRunning: true).Should().Be(CourierSpawnDecision.AlreadyRunning);
    }

    [Fact]
    public void A_manual_drain_holding_its_lease_refuses_even_an_urgent_item()
    {
        Decide(manualDrainLeaseHeld: true, hasUrgentItem: true).Should().Be(CourierSpawnDecision.ManualDrainInProgress);
    }

    [Fact]
    public void The_day_cap_refuses_once_reached_even_for_an_urgent_item()
    {
        Decide(hasUrgentItem: true, spawnsToday: 500, daySpawnCap: 500).Should().Be(CourierSpawnDecision.DayCapReached);
    }

    [Fact]
    public void Below_the_day_cap_still_spawns()
    {
        Decide(spawnsToday: 499, daySpawnCap: 500, elapsedSinceLastCourier: null).Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void No_prior_courier_ever_spawns_at_once()
    {
        Decide(elapsedSinceLastCourier: null, quietFor: TimeSpan.FromSeconds(1)).Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void A_quiet_feed_for_ten_minutes_spawns_immediately_after_the_last_courier()
    {
        Decide(elapsedSinceLastCourier: TimeSpan.FromMilliseconds(1), quietFor: TimeSpan.FromMinutes(10))
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void A_busy_feed_still_waiting_out_the_ceiling_refuses()
    {
        // Busiest possible (quietFor = 0) means the wait is the full 60s ceiling; only 10s have
        // passed since the last courier.
        Decide(elapsedSinceLastCourier: TimeSpan.FromSeconds(10), quietFor: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void A_busy_feed_that_has_waited_out_the_full_ceiling_spawns()
    {
        Decide(elapsedSinceLastCourier: TimeSpan.FromSeconds(60), quietFor: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void An_urgent_item_bypasses_a_ceiling_wait_that_has_not_elapsed()
    {
        Decide(hasUrgentItem: true, elapsedSinceLastCourier: TimeSpan.FromSeconds(1), quietFor: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void An_urgent_item_already_carried_by_a_failed_courier_waits_out_the_step_like_any_other()
    {
        // Without the carried-sequence check this would spawn at once every sweep tick with no
        // backoff at all, the runaway the day cap alone used to be left to stop.
        Decide(
                hasUrgentItem: true, urgentSequence: 50, streak: Streak(1, TimeSpan.FromSeconds(1), carried: 50),
                elapsedSinceLastCourier: TimeSpan.FromSeconds(1))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void An_urgent_item_already_carried_spawns_once_the_step_has_elapsed()
    {
        Decide(
                hasUrgentItem: true, urgentSequence: 50, streak: Streak(1, TimeSpan.FromSeconds(60), carried: 50),
                elapsedSinceLastCourier: TimeSpan.FromSeconds(60))
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void A_failed_project_never_rides_the_quiet_ramp_down_to_zero()
    {
        // The undelivered item's own age is what quietFor measures, and it only grows while
        // nothing new arrives, so on the ordinary ramp a failed delivery would respawn on every
        // sweep tick once the feed looked quiet for ten minutes.
        Decide(
                streak: Streak(3, TimeSpan.FromSeconds(1)),
                elapsedSinceLastCourier: TimeSpan.FromSeconds(1), quietFor: TimeSpan.FromMinutes(15))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void A_failed_project_spawns_again_once_its_next_attempt_time_has_passed()
    {
        Decide(
                streak: Streak(3, TimeSpan.FromMinutes(4)),
                elapsedSinceLastCourier: TimeSpan.FromMinutes(4), quietFor: TimeSpan.FromMinutes(15))
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void A_courier_max_wait_of_zero_does_not_let_a_failing_project_respawn_on_every_tick()
    {
        // The batching wait is zero here, but the streak's next attempt was derived from the
        // sixty second floor, so a tick ten seconds after the failure waits.
        Decide(
                maxWait: TimeSpan.Zero, streak: Streak(1, TimeSpan.FromSeconds(10)),
                elapsedSinceLastCourier: TimeSpan.FromSeconds(10), quietFor: TimeSpan.FromMinutes(15))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void An_urgent_item_no_failed_courier_carried_gets_one_early_attempt_at_any_step()
    {
        // The sixth failure's wait is thirty minutes, and the courier completed two minutes ago.
        Decide(
                hasUrgentItem: true, urgentSequence: 51, streak: Streak(6, TimeSpan.FromMinutes(2), carried: 50),
                elapsedSinceLastCourier: TimeSpan.FromMinutes(2))
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void The_early_attempt_waits_until_at_least_the_base_has_passed_since_the_last_courier()
    {
        // Two urgent items ten seconds apart during a streak: the first got its attempt, and the
        // second must not spawn another courier until the base interval has passed.
        Decide(
                hasUrgentItem: true, urgentSequence: 52, streak: Streak(6, TimeSpan.FromSeconds(10), carried: 50),
                elapsedSinceLastCourier: TimeSpan.FromSeconds(10))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void The_early_attempt_does_not_reset_the_doubling_and_the_item_gets_no_second_bypass()
    {
        // The early courier carried through 51 and failed; the streak is one step further up and
        // records 51 as carried, so the same item no longer qualifies.
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30));
        CourierFailureStreak before = Streak(6, TimeSpan.FromMinutes(2), carried: 50);
        CourierFailureStreak after = CourierFailureStreak.AfterFailure(
            before, ProjectId, Now, Now.AddSeconds(-30), "not reachable", carriedThroughSequence: 51, waits);

        after.Count.Should().Be(7, "the doubling is not reset, only the next step up taken");
        Decide(
                hasUrgentItem: true, urgentSequence: 51, streak: after,
                elapsedSinceLastCourier: TimeSpan.FromMinutes(2))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void The_early_attempt_applies_to_a_streak_only_not_to_a_zero_count_hold()
    {
        // A courier the daemon itself ended holds the gate for one base wait, urgent or not.
        CourierFailureStreak hold = CourierFailureStreak.AfterDaemonEnding(
            null, ProjectId, Now, CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30)));

        Decide(hasUrgentItem: true, streak: hold, elapsedSinceLastCourier: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Waiting);
        Decide(streak: hold, elapsedSinceLastCourier: TimeSpan.Zero, quietFor: TimeSpan.FromMinutes(15))
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void A_zero_count_hold_that_has_elapsed_hands_back_to_the_ordinary_wait()
    {
        CourierFailureStreak hold = CourierFailureStreak.AfterDaemonEnding(
            null, ProjectId, Now - TimeSpan.FromMinutes(5), CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30)));

        Decide(hasUrgentItem: true, streak: hold, elapsedSinceLastCourier: TimeSpan.FromMinutes(5))
            .Should().Be(CourierSpawnDecision.Spawn);
    }

    [Fact]
    public void The_day_cap_still_refuses_a_failing_project_with_an_early_attempt_available()
    {
        Decide(
                hasUrgentItem: true, urgentSequence: 51, streak: Streak(6, TimeSpan.FromMinutes(2), carried: 50),
                elapsedSinceLastCourier: TimeSpan.FromMinutes(2), spawnsToday: 500)
            .Should().Be(CourierSpawnDecision.DayCapReached);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(300, 30)]
    [InlineData(600, 0)]
    [InlineData(900, 0)]
    public void Wait_ramps_linearly_from_the_ceiling_down_to_zero_over_the_quiet_threshold(
        int quietForSeconds, int expectedWaitSeconds)
    {
        TimeSpan wait = CourierGate.Wait(TimeSpan.FromSeconds(quietForSeconds), QuietThreshold, MaxWait);

        wait.Should().BeCloseTo(TimeSpan.FromSeconds(expectedWaitSeconds), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Wait_is_zero_when_the_quiet_threshold_itself_is_zero()
    {
        CourierGate.Wait(TimeSpan.Zero, TimeSpan.Zero, MaxWait).Should().Be(TimeSpan.Zero);
    }
}
