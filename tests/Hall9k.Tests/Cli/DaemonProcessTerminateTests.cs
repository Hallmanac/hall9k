using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Regression coverage for independent pre-PR review cycle 1's adversarial finding: commit
/// 8bbec3ed folded <c>ArgumentException</c>, <c>InvalidOperationException</c> and
/// <c>Win32Exception</c> into the same catch clause added for <c>AggregateException</c> and
/// returned <c>true</c> for all four, so a session already ended on its own (or a pid the OS
/// has since recycled) would read as "terminated" instead of "nothing was there to end" —
/// disabling <c>RunKillCommand</c>'s guard against recording a kill nobody performed.
/// </summary>
public sealed class DaemonProcessTerminateTests
{
    [Fact]
    public void A_pid_this_machine_has_no_matching_process_for_is_not_reported_terminated()
    {
        // int.MaxValue - 1 (Decisions Log #2's own honesty test in ProcessManagerParityTests):
        // never a live pid, so Process.GetProcessById throws ArgumentException.
        DaemonProcess.Terminate(int.MaxValue - 1, DateTimeOffset.UtcNow).Should().BeFalse(
            "nothing was actually ended, so this must not read the same as a real kill");
    }
}
