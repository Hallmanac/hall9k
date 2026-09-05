using FluentAssertions;
using Hall9k.Connectors.Verification;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// <see cref="AdHocGateRunner"/> answers one narrow question — does this command exit zero, exit
/// nonzero, or never even reach a verdict, spawned once, in this directory — for both of its
/// callers (a project's own <c>--verify</c> gate validated against a clean base checkout at set
/// time, and a run's failed gate re-run there to tell "this was never going to pass" apart from a
/// bare gate failure), so these tests exercise it directly rather than through either caller's own
/// plumbing. Three of the four commands below are POSIX shell syntax that <c>cmd.exe</c> (the
/// Windows path, <see cref="AdHocGateRunner.RunAsync"/>'s own <c>OperatingSystem.IsWindows()</c>
/// branch) does not understand the same way — an unconditional exit code or an unexpected string
/// on that platform, not the behavior each test is actually checking — so each one is skipped
/// there, the same convention <c>HeadlessLaunchTests</c> already uses for its own untested Windows
/// path (independent pre-PR review, cycle 1, both lenses: these ran POSIX-only and would fail
/// deterministically on the windows-latest CI leg, which runs every non-RequiresDocker unit test).
/// </summary>
public sealed class AdHocGateRunnerTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-adhoc-gate-").FullName;

    [Fact]
    public async Task A_command_that_exits_zero_passes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "echo all-good", TimeSpan.FromSeconds(10), cts.Token);

        result.Outcome.Should().Be(GateCheckOutcome.Passed);
        result.OutputTail.Should().Contain("all-good");
    }

    [Fact]
    public async Task A_command_that_exits_nonzero_fails_and_carries_its_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "echo something-broke; exit 1", TimeSpan.FromSeconds(10), cts.Token);

        result.Outcome.Should().Be(GateCheckOutcome.Failed);
        result.OutputTail.Should().Contain("something-broke");
    }

    [Fact]
    public async Task A_command_that_overruns_its_timeout_is_inconclusive_rather_than_hangs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "sleep 30", TimeSpan.FromSeconds(1), cts.Token);

        // A timeout means the command's own verdict was never observed, not that it failed
        // (independent pre-PR review, cycle 1, adversarial finding) — this attempt could not
        // start, or run, or overran its budget, none of which say the gate itself is broken.
        result.Outcome.Should().Be(GateCheckOutcome.Inconclusive);
        result.OutputTail.Should().Contain("timeout");

        // Rounding a sub-minute timeout to whole minutes used to report "0-minute timeout" —
        // never the value actually configured (independent pre-PR review, cycle 2, adversarial
        // lens — Copilot).
        result.OutputTail.Should().NotContain("0-minute");
        result.OutputTail.Should().Contain("1-second");
    }

    [Fact]
    public async Task A_command_with_more_output_than_the_tail_budget_still_reports_only_the_tail()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Reading the whole redirected log into memory before tailing it would defeat the bounded
        // heap this method's redirect-to-file design exists for (independent pre-PR review, cycle
        // 2, adversarial lens — Copilot).
        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "yes line | head -c 20000", TimeSpan.FromSeconds(10), cts.Token);

        result.Outcome.Should().Be(GateCheckOutcome.Passed);
        result.OutputTail.Length.Should().BeLessThanOrEqualTo(400);
    }

    [Fact]
    public async Task The_command_runs_in_the_given_working_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        File.WriteAllText(Path.Combine(_directory, "marker.txt"), "here\n");

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "test -f marker.txt && echo found || echo missing", TimeSpan.FromSeconds(10), cts.Token);

        result.Outcome.Should().Be(GateCheckOutcome.Passed);
        result.OutputTail.Should().Contain("found");
    }
}
