using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The wait after n failed couriers in a row, <c>min(base x 2^(n-1), ceiling)</c>, with the base
/// floored at sixty seconds and the ceiling floored at the base.
/// </summary>
public sealed class CourierBackoffWaitsTests
{
    private static readonly TimeSpan DefaultCeiling = TimeSpan.FromMinutes(30);

    private static double[] Minutes(CourierBackoffWaits waits, int failures) =>
        [.. Enumerable.Range(1, failures).Select(n => waits.Step(n).TotalMinutes)];

    [Fact]
    public void At_the_defaults_the_waits_are_one_two_four_eight_sixteen_then_thirty_minutes()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, DefaultCeiling);

        Minutes(waits, 8).Should().Equal(1, 2, 4, 8, 16, 30, 30, 30);
    }

    [Fact]
    public void A_project_that_never_delivers_spends_about_fifty_couriers_a_day_not_the_day_cap()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, DefaultCeiling);

        double elapsedMinutes = 0;
        int couriers = 0;
        for (int n = 1; elapsedMinutes < 24 * 60; n++)
        {
            elapsedMinutes += waits.Step(n).TotalMinutes;
            couriers++;
        }

        couriers.Should().BeInRange(45, 55);
    }

    [Fact]
    public void A_courier_max_wait_of_zero_still_waits_a_full_minute_after_the_first_failure()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(0, DefaultCeiling);

        waits.Base.Should().Be(TimeSpan.FromSeconds(60));
        Minutes(waits, 7).Should().Equal(1, 2, 4, 8, 16, 30, 30);
    }

    [Fact]
    public void A_courier_max_wait_above_the_ceiling_lifts_the_ceiling_to_the_base()
    {
        // 45 minutes: the ceiling is never less than the base, so the wait never shrinks below it.
        CourierBackoffWaits waits = CourierBackoffWaits.For(45 * 60, DefaultCeiling);

        waits.Base.Should().Be(TimeSpan.FromMinutes(45));
        waits.Ceiling.Should().Be(TimeSpan.FromMinutes(45));
        Minutes(waits, 4).Should().Equal(45, 45, 45, 45);
    }

    [Fact]
    public void A_courier_max_wait_between_the_floor_and_the_ceiling_is_the_base()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(120, DefaultCeiling);

        Minutes(waits, 6).Should().Equal(2, 4, 8, 16, 30, 30);
    }

    [Fact]
    public void A_configured_ceiling_below_the_base_is_lifted_to_it()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, TimeSpan.FromSeconds(5));

        waits.Ceiling.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_step_with_no_failures_is_the_base()
    {
        CourierBackoffWaits.For(null, DefaultCeiling).Step(0).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void A_very_long_streak_cannot_overflow_and_sits_at_the_ceiling()
    {
        CourierBackoffWaits waits = CourierBackoffWaits.For(null, DefaultCeiling);

        waits.Step(500).Should().Be(DefaultCeiling);
        waits.Step(int.MaxValue).Should().Be(DefaultCeiling);
    }
}
