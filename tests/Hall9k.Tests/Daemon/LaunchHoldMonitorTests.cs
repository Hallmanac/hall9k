using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The probe's own doubling backoff (task: a session that exits at once with no work done is
/// treated as the node failing to launch sessions): starts at <c>SessionErrorRetryBackoff</c>,
/// doubles each unsuccessful probe, caps at <c>LaunchHoldProbeBackoffMaxInterval</c> — the same
/// widening-with-a-floor shape <c>PullRequestPollBackoffMaxInterval</c> already gives a different
/// doorbell-less recovery.
/// </summary>
public sealed class LaunchHoldMonitorTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(15);

    [Fact]
    public void The_first_probe_waits_the_starting_backoff() =>
        LaunchHoldMonitor.BackoffForProbe(probeCount: 0, Start, Cap).Should().Be(Start);

    [Fact]
    public void Each_further_probe_doubles_the_wait()
    {
        LaunchHoldMonitor.BackoffForProbe(1, Start, Cap).Should().Be(TimeSpan.FromSeconds(180));
        LaunchHoldMonitor.BackoffForProbe(2, Start, Cap).Should().Be(TimeSpan.FromSeconds(360));
        LaunchHoldMonitor.BackoffForProbe(3, Start, Cap).Should().Be(TimeSpan.FromSeconds(720));
    }

    [Fact]
    public void The_wait_never_exceeds_the_cap() =>
        LaunchHoldMonitor.BackoffForProbe(4, Start, Cap).Should().Be(Cap, "90s doubled four times is 24 minutes, past the 15-minute cap");

    [Fact]
    public void An_outage_lasting_days_still_only_ever_waits_the_cap_with_no_overflow() =>
        LaunchHoldMonitor.BackoffForProbe(probeCount: 100_000, Start, Cap).Should().Be(
            Cap, "the loop must stop doubling once it reaches the cap rather than iterating (or overflowing TimeSpan) for every probe an outage lasting this long would have spent");
}
