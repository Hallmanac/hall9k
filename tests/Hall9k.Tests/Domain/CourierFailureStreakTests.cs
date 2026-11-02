using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The failure streak document's pure transitions: what a failed courier, a courier the daemon
/// itself ended, the first sweep tick after a daemon start, and a window registering each do to it.
/// </summary>
public sealed class CourierFailureStreakTests
{
    private static readonly Guid ProjectId = Guid.Parse("0197a000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly CourierBackoffWaits Waits = CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30));

    private static CourierFailureStreak Failed(CourierFailureStreak? before, DateTimeOffset at, string? reason = "down", long carried = 10, DateTimeOffset? addressedLaunchAt = null) =>
        CourierFailureStreak.AfterFailure(before, ProjectId, at, addressedLaunchAt ?? at.AddSeconds(-30), reason, carried, Waits);

    [Fact]
    public void The_first_failure_starts_a_streak_of_one_that_waits_the_base()
    {
        CourierFailureStreak streak = Failed(null, T0, "hall9k-9f is not reachable", carried: 42);

        streak.Count.Should().Be(1);
        streak.StartedAt.Should().Be(T0);
        streak.LastFailureAt.Should().Be(T0);
        streak.LastFailureReason.Should().Be("hall9k-9f is not reachable");
        streak.HighestCarriedSequence.Should().Be(42);
        streak.NextAttemptAt.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void Each_further_failure_counts_and_doubles_the_wait_while_keeping_the_start()
    {
        CourierFailureStreak streak = Failed(null, T0);
        for (int n = 2; n <= 7; n++)
        {
            streak = Failed(streak, T0.AddMinutes(n * 10));
        }

        streak.Count.Should().Be(7);
        streak.StartedAt.Should().Be(T0);
        streak.NextAttemptAt.Should().Be(T0.AddMinutes(70).AddMinutes(30));
    }

    [Fact]
    public void The_highest_carried_sequence_only_ever_rises()
    {
        CourierFailureStreak streak = Failed(null, T0, carried: 50);

        Failed(streak, T0.AddMinutes(1), carried: 30).HighestCarriedSequence.Should().Be(50);
        Failed(streak, T0.AddMinutes(1), carried: 80).HighestCarriedSequence.Should().Be(80);
    }

    [Fact]
    public void A_failure_after_a_zero_count_hold_starts_a_fresh_streak_at_that_failure()
    {
        CourierFailureStreak hold = CourierFailureStreak.AfterDaemonEnding(null, ProjectId, T0, Waits);

        CourierFailureStreak streak = Failed(hold, T0.AddMinutes(5));

        streak.Count.Should().Be(1);
        streak.StartedAt.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void A_courier_the_daemon_ended_changes_only_the_next_attempt_of_a_running_streak()
    {
        CourierFailureStreak streak = Failed(Failed(Failed(null, T0), T0.AddMinutes(2)), T0.AddMinutes(6), "down", carried: 77);

        CourierFailureStreak after = CourierFailureStreak.AfterDaemonEnding(streak, ProjectId, T0.AddMinutes(20), Waits);

        after.Count.Should().Be(3);
        after.StartedAt.Should().Be(streak.StartedAt);
        after.LastFailureAt.Should().Be(streak.LastFailureAt);
        after.LastFailureReason.Should().Be(streak.LastFailureReason);
        after.HighestCarriedSequence.Should().Be(77);
        after.NextAttemptAt.Should().Be(T0.AddMinutes(20).AddMinutes(4), "the third failure's step is four minutes, from the ending's own completion");
    }

    [Fact]
    public void A_courier_the_daemon_ended_with_no_streak_holds_the_gate_for_the_base_and_counts_nothing()
    {
        CourierFailureStreak hold = CourierFailureStreak.AfterDaemonEnding(null, ProjectId, T0, Waits);

        hold.Count.Should().Be(0);
        hold.NextAttemptAt.Should().Be(T0.AddMinutes(1));
        CourierFailureStreak.IsRunning(hold, launchedAt: null).Should().BeFalse();
    }

    [Fact]
    public void The_restart_pull_in_brings_a_far_next_attempt_down_to_the_tick_plus_the_base_with_the_count_unchanged()
    {
        CourierFailureStreak streak = Failed(null, T0);
        for (int n = 0; n < 8; n++)
        {
            streak = Failed(streak, T0.AddMinutes(n));
        }

        DateTimeOffset tick = T0.AddMinutes(9);
        CourierFailureStreak pulled = CourierFailureStreak.PulledIn(streak, tick, Waits);

        streak.NextAttemptAt.Should().BeAfter(tick.AddMinutes(1));
        pulled.NextAttemptAt.Should().Be(tick.AddMinutes(1));
        pulled.Count.Should().Be(streak.Count);
        pulled.StartedAt.Should().Be(streak.StartedAt);
        pulled.LastFailureAt.Should().Be(streak.LastFailureAt);
        pulled.HighestCarriedSequence.Should().Be(streak.HighestCarriedSequence);
    }

    [Fact]
    public void The_restart_pull_in_never_pushes_a_nearer_next_attempt_out()
    {
        CourierFailureStreak streak = Failed(null, T0);

        CourierFailureStreak.PulledIn(streak, T0.AddMinutes(30), Waits).Should().BeSameAs(streak);
        CourierFailureStreak.PulledIn(streak, T0, Waits).Should().BeSameAs(streak);
    }

    [Fact]
    public void A_window_launched_after_the_registration_the_last_failed_courier_addressed_resets_the_streak_and_one_launched_before_does_not()
    {
        CourierFailureStreak streak = Failed(Failed(null, T0), T0.AddMinutes(5), addressedLaunchAt: T0.AddMinutes(4));

        CourierFailureStreak.WasResetByRegistration(streak, T0.AddMinutes(6)).Should().BeTrue();
        CourierFailureStreak.IsRunning(streak, T0.AddMinutes(6)).Should().BeFalse();
        CourierFailureStreak.WasResetByRegistration(streak, T0.AddMinutes(4)).Should().BeFalse("the same registration is the address it tried");
        CourierFailureStreak.WasResetByRegistration(streak, T0.AddMinutes(-30)).Should().BeFalse();
        CourierFailureStreak.IsRunning(streak, T0.AddMinutes(-30)).Should().BeTrue();
        CourierFailureStreak.IsRunning(streak, launchedAt: null).Should().BeTrue();
    }

    [Fact]
    public void A_window_that_registered_after_the_courier_read_its_address_resets_the_streak_however_late_it_was_dispatched()
    {
        // The courier took the address of the window launched at T0+1m and failed at T0+5m; a
        // window registering at T0+1m30s, before the courier was even dispatched, is still an
        // address it never tried.
        CourierFailureStreak streak = Failed(Failed(null, T0), T0.AddMinutes(5), addressedLaunchAt: T0.AddMinutes(1));

        CourierFailureStreak.WasResetByRegistration(streak, T0.AddMinutes(1).AddSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void A_streak_stored_without_an_addressed_launch_falls_back_to_its_last_failure()
    {
        CourierFailureStreak streak = Failed(null, T0);
        streak.LastAddressedLaunchAt = null;

        CourierFailureStreak.WasResetByRegistration(streak, T0.AddSeconds(1)).Should().BeTrue();
        CourierFailureStreak.WasResetByRegistration(streak, T0.AddSeconds(-1)).Should().BeFalse();
    }

    [Fact]
    public void A_registration_reset_clears_a_streak_whose_last_ending_was_its_own_failure()
    {
        CourierFailureStreak streak = Failed(Failed(null, T0), T0.AddMinutes(5));

        CourierFailureStreak.AfterRegistrationReset(streak, lastEndedAt: T0.AddMinutes(5), Waits).Should().BeNull();
        CourierFailureStreak.AfterRegistrationReset(streak, lastEndedAt: null, Waits).Should().BeNull();
    }

    [Fact]
    public void A_registration_reset_keeps_a_base_hold_for_a_daemon_ending_after_the_last_failure()
    {
        CourierFailureStreak streak = Failed(Failed(null, T0), T0.AddMinutes(5));
        CourierFailureStreak ended = CourierFailureStreak.AfterDaemonEnding(streak, ProjectId, T0.AddMinutes(9), Waits);

        CourierFailureStreak? hold = CourierFailureStreak.AfterRegistrationReset(ended, lastEndedAt: T0.AddMinutes(9), Waits);

        hold.Should().NotBeNull();
        hold!.Count.Should().Be(0);
        hold.NextAttemptAt.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public void A_zero_count_hold_is_never_reset_by_a_registration()
    {
        CourierFailureStreak hold = CourierFailureStreak.AfterDaemonEnding(null, ProjectId, T0, Waits);

        CourierFailureStreak.WasResetByRegistration(hold, T0.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void A_reason_has_its_newlines_collapsed_to_single_spaces()
    {
        CourierFailureStreak.NormalizeReason("first line\r\nsecond line\n\nthird\r").Should().Be("first line second line third");
    }

    [Fact]
    public void A_reason_longer_than_one_hundred_twenty_characters_is_cut_with_an_ellipsis_to_exactly_that_length()
    {
        string reason = CourierFailureStreak.NormalizeReason(new string('x', 500));

        reason.Should().HaveLength(120);
        reason.Should().EndWith("…");
        reason[..119].Should().Be(new string('x', 119));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 118 characters, then an emoji (two UTF-16 units) straddling the cut.
        string reason = CourierFailureStreak.NormalizeReason(new string('x', 118) + "😀" + new string('y', 50));

        reason.Should().HaveLength(119);
        reason.Should().EndWith("x…");
        reason.Where(char.IsSurrogate).Should().BeEmpty();
    }

    [Fact]
    public void A_reason_of_exactly_one_hundred_twenty_characters_is_kept_whole()
    {
        string exact = new('y', 120);

        CourierFailureStreak.NormalizeReason(exact).Should().Be(exact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void A_blank_reason_reads_as_no_reason_given(string? reason)
    {
        CourierFailureStreak.NormalizeReason(reason).Should().Be("no reason given");
    }
}
