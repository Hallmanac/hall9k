using FluentAssertions;
using Hall9k.Daemon.AutoPrReview;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="AutoPrReviewMonitor"/> reuses <see cref="Hall9k.Daemon.Closeout.PullRequestMonitor"/>'s
/// own backoff mechanics wholesale (already proven in <c>PullRequestMonitorTests</c>) — the only
/// logic this monitor adds is its own sweep-shape failure predicate, tested here.
/// </summary>
public sealed class AutoPrReviewMonitorTests
{
    [Fact]
    public void A_sweep_is_a_failure_only_when_every_inspected_project_failed()
    {
        AutoPrReviewMonitor.IsSweepFailure(new AutoPrReviewSweepResult(3, 3, 0, 0)).Should().BeTrue();
        AutoPrReviewMonitor.IsSweepFailure(new AutoPrReviewSweepResult(3, 1, 2, 0)).Should()
            .BeFalse("one broken project must not pin every other healthy one to the backoff ceiling");
    }

    [Fact]
    public void An_empty_sweep_is_not_a_failure()
    {
        AutoPrReviewMonitor.IsSweepFailure(new AutoPrReviewSweepResult(0, 0, 0, 0)).Should()
            .BeFalse("nothing opted in is not gh trouble");
    }
}
