using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The courier's line on <c>h9k status</c>: its states, their precedence, and the failing line's
/// wording. Times are built in local time, because the line prints local clock times.
/// </summary>
public sealed class CourierStatusLineTests
{
    private static readonly Guid ProjectId = Guid.Parse("0197a000-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 2, 20, 30, 0, DateTimeKind.Local));

    private static CourierFailureStreak Streak(
        int count = 14, string reason = "hall9k-9f is not reachable", TimeSpan? nextIn = null) => new()
    {
        Id = ProjectId,
        Count = count,
        StartedAt = new DateTimeOffset(new DateTime(2026, 11, 2, 20, 1, 0, DateTimeKind.Local)),
        LastFailureAt = Now.AddMinutes(-18),
        LastFailureReason = reason,
        NextAttemptAt = Now + (nextIn ?? TimeSpan.FromMinutes(12)),
    };

    private static CourierRunDetails Running() => new()
    {
        Id = Guid.NewGuid(), ProjectId = ProjectId, DispatchedAt = Now.AddSeconds(-20),
    };

    private static CourierDaySpawnCounter CapSpent(DateTimeOffset at) => new()
    {
        Id = ProjectId, Day = DateOnly.FromDateTime(at.UtcDateTime), Count = 500, CapHitLogged = true,
    };

    private static string? Describe(
        CourierRunDetails? inFlight = null,
        DateTimeOffset? lastDeliveredAt = null,
        CourierFailureStreak? streak = null,
        CourierDaySpawnCounter? dayCounter = null,
        bool registered = true,
        DateTimeOffset? launchedAt = null) =>
        CourierStatusLine.Describe(
            new CourierStatusFacts(inFlight, lastDeliveredAt, streak, dayCounter, registered, launchedAt), Now);

    [Fact]
    public void A_project_that_has_never_run_a_courier_says_nothing()
    {
        Describe().Should().BeNull();
    }

    [Fact]
    public void A_courier_in_flight_that_never_delivered_says_so()
    {
        Describe(inFlight: Running()).Should().Be("courier: in flight (dispatched 20s ago); never delivered");
    }

    [Fact]
    public void An_idle_courier_names_its_last_delivery()
    {
        Describe(lastDeliveredAt: Now.AddHours(-2)).Should()
            .Be($"courier: idle; last delivered {Now.AddHours(-2):yyyy-MM-dd HH:mm} (2h00m ago)");
    }

    [Fact]
    public void A_failing_project_reads_with_the_count_the_start_the_next_try_and_the_last_reason()
    {
        Describe(streak: Streak()).Should().Be(
            "courier: failing, 14 in a row since 20:01, next try in 12m, last: hall9k-9f is not reachable");
    }

    [Fact]
    public void A_streak_that_began_on_an_earlier_day_names_the_date()
    {
        CourierFailureStreak streak = Streak();
        streak.StartedAt = new DateTimeOffset(new DateTime(2026, 11, 1, 20, 1, 0, DateTimeKind.Local));

        Describe(streak: streak).Should().Contain("14 in a row since 2026-11-01 20:01,");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Once_the_next_attempt_time_has_passed_it_says_the_next_sweep_never_a_zero_or_negative_duration(int secondsFromNow)
    {
        string? line = Describe(streak: Streak(nextIn: TimeSpan.FromSeconds(secondsFromNow)));

        line.Should().Contain("next try at the next sweep,");
        line.Should().NotContain("next try in");
    }

    [Fact]
    public void A_wait_under_a_second_never_reads_as_zero()
    {
        Describe(streak: Streak(nextIn: TimeSpan.FromMilliseconds(400))).Should().Contain("next try in 1s,");
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(61, "2m")]
    [InlineData(1800, "30m")]
    [InlineData(3900, "1h05m")]
    public void The_next_try_countdown_rounds_up_to_whole_seconds_then_minutes(int seconds, string expected)
    {
        Describe(streak: Streak(nextIn: TimeSpan.FromSeconds(seconds))).Should().Contain($"next try in {expected},");
    }

    [Fact]
    public void A_failing_line_is_only_for_a_registered_presence()
    {
        Describe(streak: Streak(), registered: false).Should().BeNull();
    }

    [Fact]
    public void A_zero_count_hold_is_not_failing()
    {
        Describe(streak: Streak(count: 0)).Should().BeNull();
    }

    [Fact]
    public void A_window_launched_after_the_last_failure_has_reset_the_streak_so_the_line_is_not_failing()
    {
        Describe(streak: Streak(), launchedAt: Now.AddMinutes(-1)).Should().BeNull();
        Describe(streak: Streak(), launchedAt: Now.AddMinutes(-60)).Should().StartWith("courier: failing,");
    }

    [Fact]
    public void A_reason_with_newlines_and_terminal_escapes_prints_on_one_line_without_them()
    {
        string? line = Describe(streak: Streak(reason: "first line\nsecond \u001b[31mred\u001b[0m line"));

        line.Should().NotContain("\n").And.NotContain("\u001b");
        line.Should().Contain("last: first line");
    }

    [Fact]
    public void A_spent_day_cap_gives_the_local_time_the_day_rolls()
    {
        DateTimeOffset rolls = CourierDaySpawnCounter.RollsOverAt(DateOnly.FromDateTime(Now.UtcDateTime));

        Describe(dayCounter: CapSpent(Now)).Should().Be(
            $"courier: day cap spent, rolls over at {rolls.ToLocalTime():HH:mm} (00:00 UTC); never delivered");
        rolls.UtcDateTime.TimeOfDay.Should().Be(TimeSpan.Zero);
        rolls.Should().BeAfter(Now);
    }

    [Fact]
    public void A_cap_recorded_on_an_earlier_utc_day_is_not_spent_today()
    {
        Describe(dayCounter: CapSpent(Now.AddDays(-1))).Should().BeNull();
    }

    [Fact]
    public void In_flight_outranks_day_cap_spent_and_failing_and_appends_the_streak_count()
    {
        Describe(inFlight: Running(), streak: Streak(), dayCounter: CapSpent(Now)).Should()
            .Be("courier: in flight (dispatched 20s ago), 14 failed in a row; never delivered");
    }

    [Fact]
    public void Day_cap_spent_outranks_failing_and_appends_the_streak_count()
    {
        string? line = Describe(streak: Streak(), dayCounter: CapSpent(Now));

        line.Should().StartWith("courier: day cap spent, rolls over at ").And.Contain(", 14 failed in a row; never delivered");
        line.Should().NotContain("failing");
    }

    [Fact]
    public void A_state_that_wins_over_an_unregistered_streak_does_not_count_it()
    {
        Describe(inFlight: Running(), streak: Streak(), registered: false).Should()
            .Be("courier: in flight (dispatched 20s ago); never delivered");
    }
}
