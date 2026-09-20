using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>The feed courier's own per-project, per-day spawn counter (idea 89471598, piece 3) — pure bookkeeping, no store.</summary>
public sealed class CourierDaySpawnCounterTests
{
    private static readonly Guid ProjectId = DomainId.New();
    private static readonly DateOnly Today = new(2026, 9, 20);
    private static readonly DateOnly Yesterday = new(2026, 9, 19);

    [Fact]
    public void No_counter_yet_reads_as_zero_for_today()
    {
        CourierDaySpawnCounter.CountFor(null, Today).Should().Be(0);
    }

    [Fact]
    public void A_counter_from_a_prior_day_reads_as_zero_for_today()
    {
        CourierDaySpawnCounter stale = new() { Id = ProjectId, Day = Yesterday, Count = 17 };

        CourierDaySpawnCounter.CountFor(stale, Today).Should().Be(0);
    }

    [Fact]
    public void A_counter_from_today_reads_its_own_count()
    {
        CourierDaySpawnCounter fromToday = new() { Id = ProjectId, Day = Today, Count = 3 };

        CourierDaySpawnCounter.CountFor(fromToday, Today).Should().Be(3);
    }

    [Fact]
    public void Incrementing_with_no_prior_counter_starts_at_one()
    {
        CourierDaySpawnCounter incremented = CourierDaySpawnCounter.Incremented(null, ProjectId, Today);

        incremented.Id.Should().Be(ProjectId);
        incremented.Day.Should().Be(Today);
        incremented.Count.Should().Be(1);
        incremented.CapHitLogged.Should().BeFalse();
    }

    [Fact]
    public void Incrementing_a_prior_days_counter_rolls_over_to_one_rather_than_adding()
    {
        CourierDaySpawnCounter stale = new() { Id = ProjectId, Day = Yesterday, Count = 500, CapHitLogged = true };

        CourierDaySpawnCounter incremented = CourierDaySpawnCounter.Incremented(stale, ProjectId, Today);

        incremented.Day.Should().Be(Today);
        incremented.Count.Should().Be(1);
        incremented.CapHitLogged.Should().BeFalse("a new day starts the cap-hit log fresh too");
    }

    [Fact]
    public void Incrementing_todays_counter_adds_one_and_keeps_the_cap_hit_flag()
    {
        CourierDaySpawnCounter today = new() { Id = ProjectId, Day = Today, Count = 4, CapHitLogged = true };

        CourierDaySpawnCounter incremented = CourierDaySpawnCounter.Incremented(today, ProjectId, Today);

        incremented.Count.Should().Be(5);
        incremented.CapHitLogged.Should().BeTrue();
    }
}
