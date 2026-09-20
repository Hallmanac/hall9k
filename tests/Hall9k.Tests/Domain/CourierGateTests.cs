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

    private static CourierSpawnDecision Decide(
        bool hasUndrainedItems = true,
        bool orchestratorLive = true,
        bool courierAlreadyRunning = false,
        bool manualDrainLeaseHeld = false,
        bool hasUrgentItem = false,
        bool lastCourierFailed = false,
        TimeSpan? elapsedSinceLastCourier = null,
        TimeSpan? quietFor = null,
        int spawnsToday = 0,
        int daySpawnCap = 500) =>
        CourierGate.Decide(
            hasUndrainedItems, orchestratorLive, courierAlreadyRunning, manualDrainLeaseHeld, hasUrgentItem,
            lastCourierFailed, elapsedSinceLastCourier, quietFor ?? TimeSpan.Zero, QuietThreshold, MaxWait,
            spawnsToday, daySpawnCap);

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
    public void An_urgent_item_after_a_failed_delivery_waits_out_the_ceiling_like_any_other()
    {
        // Without lastCourierFailed's own check, this would spawn at once every sweep tick with
        // no backoff at all — the exact runaway the day cap alone used to be left to stop.
        Decide(
                hasUrgentItem: true, lastCourierFailed: true,
                elapsedSinceLastCourier: TimeSpan.FromSeconds(1), quietFor: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Waiting);
    }

    [Fact]
    public void An_urgent_item_after_a_failed_delivery_spawns_once_the_wait_elapses()
    {
        Decide(
                hasUrgentItem: true, lastCourierFailed: true,
                elapsedSinceLastCourier: TimeSpan.FromSeconds(60), quietFor: TimeSpan.Zero)
            .Should().Be(CourierSpawnDecision.Spawn);
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
