using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>The doctor's courier verdicts for one project, from hand-supplied facts: nothing here reads a database or a feed.</summary>
public sealed class CourierDoctorTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 2, 20, 30, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset LaunchedLongAgo = Now.AddHours(-5);

    private static CourierFailureStreak Streak(int count, string reason = "hall9k-9f is not reachable") => new()
    {
        Id = Guid.NewGuid(),
        Count = count,
        StartedAt = Now.AddHours(-2),
        LastFailureAt = Now.AddMinutes(-18),
        LastFailureReason = reason,
        NextAttemptAt = Now.AddMinutes(12),
    };

    private static CourierDoctorFacts Facts(
        CourierFailureStreak? streak = null,
        CourierDaySpawnCounter? dayCounter = null,
        DateTimeOffset? oldestUndrainedAt = null,
        bool windowLive = true,
        DateTimeOffset? launchedAt = null) =>
        new("hall9k", windowLive, launchedAt ?? LaunchedLongAgo, streak, dayCounter, oldestUndrainedAt);

    [Fact]
    public void A_healthy_project_gets_one_ok_line()
    {
        CourierDoctor.Evaluate(Facts(), Now).Should().ContainSingle().Which.Should().Contain("hall9k: courier ok");
    }

    [Fact]
    public void A_streak_of_five_in_a_row_warns_with_the_status_lines_reason()
    {
        IReadOnlyList<string> lines = CourierDoctor.Evaluate(Facts(streak: Streak(5)), Now);

        lines.Should().ContainSingle().Which.Should()
            .Contain("warn:").And.Contain("hall9k: courier failing, 5 in a row since ")
            .And.Contain("last: hall9k-9f is not reachable");
    }

    [Fact]
    public void A_streak_of_four_does_not_warn()
    {
        CourierDoctor.Evaluate(Facts(streak: Streak(4)), Now).Should().ContainSingle().Which.Should().Contain("courier ok");
    }

    [Fact]
    public void A_streak_a_newer_window_registration_has_reset_does_not_warn()
    {
        CourierDoctor.Evaluate(Facts(streak: Streak(9), launchedAt: Now.AddMinutes(-2)), Now)
            .Should().ContainSingle().Which.Should().Contain("courier ok");
    }

    [Fact]
    public void A_feed_check_that_could_not_reach_the_end_says_so_instead_of_reporting_ok()
    {
        CourierDoctor.Evaluate(Facts() with { FeedCheckIncomplete = true }, Now).Should().ContainSingle().Which.Should()
            .Contain("undrained-item check could not reach the end").And.NotContain("courier ok");
    }

    [Fact]
    public void A_spent_day_cap_warns_with_when_it_rolls()
    {
        CourierDaySpawnCounter capped = new()
        {
            Id = Guid.NewGuid(), Day = DateOnly.FromDateTime(Now.UtcDateTime), Count = 500, CapHitLogged = true,
        };

        CourierDoctor.Evaluate(Facts(dayCounter: capped), Now).Should().ContainSingle().Which.Should()
            .Contain("warn:").And.Contain("hall9k: courier day cap spent, rolls over at ").And.Contain("(00:00 UTC)");
    }

    [Fact]
    public void An_item_undrained_for_over_an_hour_under_a_live_window_warns()
    {
        CourierDoctor.Evaluate(Facts(oldestUndrainedAt: Now.AddMinutes(-90)), Now).Should().ContainSingle().Which.Should()
            .Contain("warn:").And.Contain("a feed item has sat undrained for 1h30m");
    }

    [Fact]
    public void An_item_undrained_for_under_an_hour_does_not_warn()
    {
        CourierDoctor.Evaluate(Facts(oldestUndrainedAt: Now.AddMinutes(-59)), Now)
            .Should().ContainSingle().Which.Should().Contain("courier ok");
    }

    [Fact]
    public void An_undrained_item_does_not_warn_when_the_window_is_not_live()
    {
        CourierDoctor.Evaluate(Facts(oldestUndrainedAt: Now.AddHours(-3), windowLive: false), Now)
            .Should().ContainSingle().Which.Should().Contain("courier ok");
    }

    [Fact]
    public void An_undrained_item_does_not_warn_when_the_window_registered_less_than_an_hour_ago()
    {
        CourierDoctor.Evaluate(Facts(oldestUndrainedAt: Now.AddHours(-3), launchedAt: Now.AddMinutes(-40)), Now)
            .Should().ContainSingle().Which.Should().Contain("courier ok");
    }

    [Fact]
    public void A_project_already_warned_for_its_streak_is_not_warned_again_for_the_undrained_hour()
    {
        IReadOnlyList<string> lines = CourierDoctor.Evaluate(
            Facts(streak: Streak(8), oldestUndrainedAt: Now.AddHours(-3)), Now);

        lines.Should().ContainSingle().Which.Should().Contain("courier failing, 8 in a row");
        lines[0].Should().NotContain("undrained");
    }

    [Fact]
    public void A_spent_cap_and_an_undrained_hour_each_warn_when_no_streak_does()
    {
        CourierDaySpawnCounter capped = new()
        {
            Id = Guid.NewGuid(), Day = DateOnly.FromDateTime(Now.UtcDateTime), Count = 500, CapHitLogged = true,
        };

        CourierDoctor.Evaluate(Facts(dayCounter: capped, oldestUndrainedAt: Now.AddHours(-2)), Now).Should().HaveCount(2);
    }

    [Fact]
    public void The_feed_is_only_read_when_an_undrained_warning_could_follow()
    {
        CourierDoctor.ShouldReadFeed(true, LaunchedLongAgo, null, Now).Should().BeTrue();
        CourierDoctor.ShouldReadFeed(false, LaunchedLongAgo, null, Now).Should().BeFalse("the window is gone");
        CourierDoctor.ShouldReadFeed(true, Now.AddMinutes(-10), null, Now).Should().BeFalse("registered under an hour ago");
        CourierDoctor.ShouldReadFeed(true, LaunchedLongAgo, Streak(6), Now).Should().BeFalse("the streak already warns");
        CourierDoctor.ShouldReadFeed(true, LaunchedLongAgo, Streak(2), Now).Should().BeTrue("two failures do not warn");
    }

    [Fact]
    public void A_reason_cannot_inject_markup_or_a_second_line()
    {
        IReadOnlyList<string> lines = CourierDoctor.Evaluate(
            Facts(streak: Streak(6, reason: "[red]boom[/]\nsecond")), Now);

        lines.Should().ContainSingle().Which.Should().Contain("[[red]]boom[[/]]").And.NotContain("\n");
    }
}
