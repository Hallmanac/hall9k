using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A failed host-coupled gate never races a clean-base comparison of itself: that comparison
/// spawns the identical gate command a second time, outside the node's one serialized
/// host-coupled slot — the exact shape of the origin incident this task answers, where the
/// daemon's own comparison held every host-coupled permit for a dead gate's whole life while a
/// dozen other classes queued behind it. <see cref="VerificationRunner.DescribeHostCoupledComparisonSkip"/>
/// is the pure decision <c>BuildReportedGateFailureReasonAsync</c> checks before it ever spawns
/// anything, so both paths (skip for host-coupled, unchanged for ordinary) are provable here
/// without spawning a suite of any kind.
/// </summary>
public sealed class HostCoupledCleanBaseComparisonTests
{
    [Fact]
    public void A_host_coupled_gates_comparison_is_skipped_with_a_stated_reason()
    {
        VerifyCommand gate = new("integration", "dotnet test", "Category=RequiresDocker");

        string? note = VerificationRunner.DescribeHostCoupledComparisonSkip(gate);

        note.Should().Be(VerificationRunner.HostCoupledComparisonSkippedNote);
        note.Should().Contain("second host-coupled");
        note.Should().Contain("serialized host gate");
    }

    [Fact]
    public void An_ordinary_gates_comparison_is_never_skipped_by_this_check()
    {
        VerifyCommand gate = new("test", "dotnet test");

        VerificationRunner.DescribeHostCoupledComparisonSkip(gate).Should().BeNull(
            "an ordinary gate's failure keeps its clean-base comparison exactly as today");
    }
}
