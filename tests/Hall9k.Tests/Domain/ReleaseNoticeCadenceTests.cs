using FluentAssertions;
using Hall9k.Domain.Features.Node;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// When the daemon notifies about a release, as the pure decision over what the node last
/// recorded: a first notice at the next check, a reminder only on the first check at least a day
/// later while still behind, a newer release restarting the clock, and an update ending it. The
/// decision reads nothing but its arguments, so a restart (a fresh caller holding the same
/// recorded state) cannot repeat a notice or reset the clock.
/// </summary>
public sealed class ReleaseNoticeCadenceTests
{
    private static readonly DateTimeOffset Noticed = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_newer_release_nobody_was_told_about_is_a_first_notice_at_the_next_check() =>
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", lastNoticeTag: null, lastNoticeAt: null, Noticed)
            .Should().Be(ReleaseNoticeKind.First);

    [Fact]
    public void The_same_release_is_quiet_for_the_first_day()
    {
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(6))
            .Should().Be(ReleaseNoticeKind.None);
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(24).AddTicks(-1))
            .Should().Be(ReleaseNoticeKind.None);
    }

    [Fact]
    public void A_reminder_lands_on_the_first_check_at_or_past_twenty_four_hours()
    {
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(24))
            .Should().Be(ReleaseNoticeKind.Reminder);
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(30))
            .Should().Be(ReleaseNoticeKind.Reminder, "a check that is late still reminds; it does not wait another day");
    }

    [Fact]
    public void A_reminder_restarts_the_clock_so_the_next_one_is_a_day_after_it()
    {
        DateTimeOffset reminded = Noticed.AddHours(30);

        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", reminded, reminded.AddHours(6))
            .Should().Be(ReleaseNoticeKind.None);
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", reminded, reminded.AddHours(24))
            .Should().Be(ReleaseNoticeKind.Reminder);
    }

    [Fact]
    public void A_still_newer_release_is_a_first_notice_at_the_next_check_whatever_the_clock_says() =>
        ReleaseNoticeCadence.Decide("v0.13.2", "0.13.0", "v0.13.1", Noticed, Noticed.AddMinutes(10))
            .Should().Be(ReleaseNoticeKind.First);

    [Fact]
    public void A_node_on_the_newest_release_is_told_nothing_however_old_the_last_notice()
    {
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.1", "v0.13.1", Noticed, Noticed.AddDays(3))
            .Should().Be(ReleaseNoticeKind.None);
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.2", "v0.13.1", Noticed, Noticed.AddDays(3))
            .Should().Be(ReleaseNoticeKind.None);
    }

    [Fact]
    public void A_local_build_past_the_tag_is_told_nothing()
    {
        ReleaseNoticeCadence.Decide("v0.13.0", "0.13.0-5-gabc1234", null, null, Noticed)
            .Should().Be(ReleaseNoticeKind.None);
    }

    [Fact]
    public void A_restart_neither_repeats_a_notice_nor_resets_the_clock()
    {
        // The only state is what the node recorded. A daemon that restarts at hour 10 reads the same
        // tag and time back, so it stays quiet, and the reminder still falls due at hour 24 rather
        // than a day after the restart.
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(10))
            .Should().Be(ReleaseNoticeKind.None);
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", Noticed, Noticed.AddHours(24))
            .Should().Be(ReleaseNoticeKind.Reminder);
    }

    [Fact]
    public void A_node_whose_notice_has_no_time_is_told_again_rather_than_never() =>
        ReleaseNoticeCadence.Decide("v0.13.1", "0.13.0", "v0.13.1", lastNoticeAt: null, now: Noticed)
            .Should().Be(ReleaseNoticeKind.First);
}
