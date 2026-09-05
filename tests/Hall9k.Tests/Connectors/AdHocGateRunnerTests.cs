using FluentAssertions;
using Hall9k.Connectors.Verification;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// <see cref="AdHocGateRunner"/> answers one narrow question — does this command exit zero, once,
/// in this directory — for both of its callers (a project's own <c>--verify</c> gate validated
/// against a clean base checkout at set time, and a run's failed gate re-run there to tell "this
/// was never going to pass" apart from a bare gate failure), so these tests exercise it directly
/// rather than through either caller's own plumbing.
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

        result.Passed.Should().BeTrue();
        result.OutputTail.Should().Contain("all-good");
    }

    [Fact]
    public async Task A_command_that_exits_nonzero_fails_and_carries_its_output()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "echo something-broke; exit 1", TimeSpan.FromSeconds(10), cts.Token);

        result.Passed.Should().BeFalse();
        result.OutputTail.Should().Contain("something-broke");
    }

    [Fact]
    public async Task A_command_that_overruns_its_timeout_fails_rather_than_hangs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "sleep 30", TimeSpan.FromSeconds(1), cts.Token);

        result.Passed.Should().BeFalse();
        result.OutputTail.Should().Contain("timeout");
    }

    [Fact]
    public async Task The_command_runs_in_the_given_working_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        File.WriteAllText(Path.Combine(_directory, "marker.txt"), "here\n");

        GateCheckResult result = await AdHocGateRunner.RunAsync(
            _directory, "test -f marker.txt && echo found || echo missing", TimeSpan.FromSeconds(10), cts.Token);

        result.Passed.Should().BeTrue();
        result.OutputTail.Should().Contain("found");
    }
}
