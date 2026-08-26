using FluentAssertions;
using Hall9k.Daemon.Closeout;
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
}
