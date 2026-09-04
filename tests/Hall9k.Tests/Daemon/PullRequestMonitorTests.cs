using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The polling criterion's backoff half (independent pre-PR review, cycle 3): a gh failure
/// widens the wait instead of re-hitting a failing gh every sweep forever, bounded, and any
/// clean sweep drops straight back to the base interval.
/// </summary>
public sealed class PullRequestMonitorTests
{
    private static readonly TimeSpan Base = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    [Fact]
    public void A_failed_sweep_doubles_the_current_interval() =>
        PullRequestMonitor.ApplyBackoff(Base, Base, Max, sweepFailed: true).Should().Be(TimeSpan.FromMinutes(6));

    [Fact]
    public void Consecutive_failures_keep_doubling()
    {
        TimeSpan interval = Base;
        interval = PullRequestMonitor.ApplyBackoff(interval, Base, Max, sweepFailed: true);
        interval = PullRequestMonitor.ApplyBackoff(interval, Base, Max, sweepFailed: true);
        interval = PullRequestMonitor.ApplyBackoff(interval, Base, Max, sweepFailed: true);

        interval.Should().Be(TimeSpan.FromMinutes(24), "3m -> 6m -> 12m -> 24m");
    }

    [Fact]
    public void Backoff_is_capped_at_the_configured_maximum()
    {
        TimeSpan interval = TimeSpan.FromMinutes(24);
        interval = PullRequestMonitor.ApplyBackoff(interval, Base, Max, sweepFailed: true);

        interval.Should().Be(Max, "24m would double past 30m, so it clamps instead of overshooting");
    }

    [Fact]
    public void Backoff_never_exceeds_the_cap_once_already_there()
    {
        PullRequestMonitor.ApplyBackoff(Max, Base, Max, sweepFailed: true).Should().Be(Max);
    }

    [Fact]
    public void A_clean_sweep_resets_straight_to_the_base_interval_from_anywhere()
    {
        PullRequestMonitor.ApplyBackoff(Max, Base, Max, sweepFailed: false).Should().Be(Base,
            "gh answering again leaves nothing left to be cautious about — no gradual decay");
        PullRequestMonitor.ApplyBackoff(Base, Base, Max, sweepFailed: false).Should().Be(Base);
    }

    [Fact]
    public void A_ceiling_at_or_under_the_base_interval_never_inverts_the_backoff()
    {
        PullRequestMonitor.ApplyBackoff(Base, Base, maxInterval: Base, sweepFailed: true).Should().Be(Base,
            "a ceiling equal to the base must still widen to no less than the base, never poll more often");
        PullRequestMonitor.ApplyBackoff(Base, Base, maxInterval: TimeSpan.FromMinutes(1), sweepFailed: true)
            .Should().Be(Base, "a ceiling below the base is treated as the base, not honored literally");
    }

    [Fact]
    public void A_zero_configured_ceiling_never_produces_a_zero_interval()
    {
        PullRequestMonitor.ApplyBackoff(Base, Base, maxInterval: TimeSpan.Zero, sweepFailed: true).Should().Be(Base,
            "a zero ceiling would otherwise hand PeriodicTimer.Period a value it rejects");
    }

    [Fact]
    public void A_ceiling_above_what_PeriodicTimer_accepts_is_clamped_down_to_it()
    {
        TimeSpan misconfiguredCeiling = TimeSpan.FromDays(60);
        PullRequestMonitor.ApplyBackoff(
                PullRequestMonitor.MaxSupportedInterval, Base, misconfiguredCeiling, sweepFailed: true)
            .Should().Be(PullRequestMonitor.MaxSupportedInterval,
                "a ceiling PeriodicTimer.Period cannot actually accept must never reach it, or the " +
                "assignment outside the loop's own try/catch stops the monitor for good");
    }

    [Fact]
    public void A_sweep_is_a_failure_only_when_every_attempted_inspection_failed()
    {
        PullRequestMonitor.IsSweepFailure(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0, Failures: 1))
            .Should().BeTrue("nothing this sweep attempted succeeded");
        PullRequestMonitor.IsSweepFailure(new CloseoutSweepResult(RunsInspected: 3, MergesObserved: 0, Failures: 1))
            .Should().BeFalse(
                "one poison pull request must not pin the interval when other watched runs are healthy");
        PullRequestMonitor.IsSweepFailure(new CloseoutSweepResult(RunsInspected: 2, MergesObserved: 1, Failures: 0))
            .Should().BeFalse();
        PullRequestMonitor.IsSweepFailure(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0, Failures: 0))
            .Should().BeFalse("an empty sweep has nothing to fail");
    }

    [Fact]
    public void A_purely_skipped_sweep_is_not_a_failure()
    {
        PullRequestMonitor.IsSweepFailure(
                new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0, Failures: 0, Skipped: 2))
            .Should().BeFalse(
                "a Done task reopened and then unassigned sits in the watch set returning Skipped " +
                "forever without ever calling gh, so a sweep that only sees skips has nothing that failed");
    }

    [Fact]
    public void A_skipped_run_does_not_veto_a_genuine_failure_verdict()
    {
        PullRequestMonitor.IsSweepFailure(
                new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0, Failures: 1, Skipped: 2))
            .Should().BeTrue(
                "a permanently-skipped run sitting alongside a genuinely broken pull request must not " +
                "mask a real gh outage — the skip is excluded from the check, not a veto over it");
    }

    private const string SettingName = nameof(DaemonOptions.PullRequestPollInterval);

    [Fact]
    public void A_positive_configured_interval_passes_through_unchanged()
    {
        PullRequestMonitor.ClampPollInterval(Base, SettingName, Base, NullLogger.Instance).Should().Be(Base);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_negative_configured_interval_falls_back_to_the_shipped_default(int seconds)
    {
        PullRequestMonitor.ClampPollInterval(TimeSpan.FromSeconds(seconds), SettingName, Base, NullLogger.Instance)
            .Should().Be(Base, "a zero or negative interval would otherwise hand PeriodicTimer's " +
                "constructor a value it rejects, crashing the monitor before a single sweep ran");
    }

    [Fact]
    public void A_positive_sub_millisecond_configured_interval_falls_back_to_the_shipped_default()
    {
        PullRequestMonitor.ClampPollInterval(TimeSpan.FromTicks(1), SettingName, Base, NullLogger.Instance)
            .Should().Be(Base, "a positive interval below one millisecond truncates to zero once handed " +
                "to PeriodicTimer's constructor, which rejects it exactly like zero or negative would, " +
                "crashing the monitor before a single sweep ran");
    }

    [Fact]
    public void A_configured_interval_above_what_PeriodicTimer_accepts_is_clamped_down_to_it()
    {
        PullRequestMonitor.ClampPollInterval(TimeSpan.FromDays(60), SettingName, Base, NullLogger.Instance)
            .Should().Be(PullRequestMonitor.MaxSupportedInterval,
                "PullRequestPollInterval=60 meaning minutes lands as 60 days once bound as a bare-integer " +
                "TimeSpan, which PeriodicTimer's constructor rejects outright, crashing the monitor before " +
                "a single sweep ran");
    }

    /// <summary>
    /// The clamp is generic (independent pre-PR review, cycle 1, both lenses): reused by
    /// AutoPrReviewMonitor for its own AutoPrReviewPollInterval, a misconfiguration must be
    /// reported against the setting that is actually wrong, not against PullRequestPollInterval's
    /// name and default by coincidence of a shared default value.
    /// </summary>
    [Fact]
    public void A_misconfigured_interval_is_reported_against_the_callers_own_setting_name_and_default()
    {
        TimeSpan autoPrReviewDefault = TimeSpan.FromMinutes(7);
        PullRequestMonitor.ClampPollInterval(
                TimeSpan.Zero, "AutoPrReviewPollInterval", autoPrReviewDefault, NullLogger.Instance)
            .Should().Be(autoPrReviewDefault, "the fallback is the caller's own default, not PullRequestPollInterval's");
    }
}
