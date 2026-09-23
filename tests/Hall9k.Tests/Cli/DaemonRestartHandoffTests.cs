using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The child sequence <c>h9k install --restart</c> / <c>h9k update --restart</c> hands to the
/// newly installed binary, and what it says when one of those children fails. Everything here
/// runs through a fake <see cref="RestartChildRunner"/>: no binary is spawned, no store is opened,
/// no schema is migrated and no daemon is touched — the point of the seam is that the plan and its
/// stop-at-first-failure behaviour are assertable without any of that.
/// </summary>
public sealed class DaemonRestartHandoffTests
{
    private static readonly DaemonProcessDescriptor RunningBefore = new(4242, DateTimeOffset.UtcNow);

    private const string Binary = "/somewhere/.hall9k/bin/h9k";

    /// <summary>Records what it was asked to run and answers from a queue of canned outcomes;
    /// an exhausted queue answers success, so a test that only cares about the first failure does
    /// not have to spell out the steps that never run.</summary>
    private sealed class RecordingRunner(params RestartStepResult[] outcomes)
    {
        private int _calls;

        public List<string> CommandLines { get; } = [];

        public List<string> Binaries { get; } = [];

        public Task<RestartStepResult> RunAsync(
            string binary, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Binaries.Add(binary);
            CommandLines.Add($"h9k {string.Join(' ', arguments)}");
            RestartStepResult result = _calls < outcomes.Length
                ? outcomes[_calls]
                : RestartStepResult.Exited(ExitCodes.Ok);
            _calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public void The_plan_is_stop_then_repair_the_schema_then_start()
    {
        IReadOnlyList<RestartStep> steps = DaemonRestartHandoff.PlanSteps();

        steps.Select(step => step.CommandLine).Should().Equal(
            "h9k daemon stop",
            "h9k doctor --yes",
            "h9k daemon start");
    }

    [Fact]
    public async Task Every_step_succeeding_runs_the_whole_plan_against_the_installed_binary()
    {
        RecordingRunner runner = new();

        int exitCode = await DaemonRestartHandoff.RunAsync(
            Binary, RunningBefore, runner.RunAsync, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        runner.CommandLines.Should().Equal("h9k daemon stop", "h9k doctor --yes", "h9k daemon start");
        runner.Binaries.Should().AllBe(Binary, "every step of the restart runs in the release that was just installed");
    }

    [Fact]
    public async Task A_failing_step_stops_the_plan_there_and_relays_its_exit_code()
    {
        RecordingRunner runner = new(RestartStepResult.Exited(ExitCodes.Ok), RestartStepResult.Exited(ExitCodes.Error));

        int exitCode = await DaemonRestartHandoff.RunAsync(
            Binary, RunningBefore, runner.RunAsync, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Error);
        runner.CommandLines.Should().Equal(
            ["h9k daemon stop", "h9k doctor --yes"],
            "a schema the doctor could not bring current is not a schema to start the daemon against");
    }

    [Fact]
    public async Task A_step_that_fails_with_a_nonzero_code_of_its_own_relays_that_code_rather_than_a_generic_one()
    {
        RecordingRunner runner = new(RestartStepResult.Exited(ExitCodes.BusinessRule));

        int exitCode = await DaemonRestartHandoff.RunAsync(
            Binary, RunningBefore, runner.RunAsync, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.BusinessRule);
    }

    [Fact]
    public async Task A_first_step_that_never_launched_is_reported_as_leaving_the_old_daemon_running()
    {
        RecordingRunner runner = new(RestartStepResult.NotLaunched("the swap placed no h9k there"));

        int exitCode = await DaemonRestartHandoff.RunAsync(
            Binary, RunningBefore, runner.RunAsync, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Error);
        runner.CommandLines.Should().Equal("h9k daemon stop");

        string message = DaemonRestartHandoff.DescribeUnlaunchableHandoff(
            Binary, RunningBefore, "the swap placed no h9k there", DaemonRestartHandoff.PlanSteps());
        message.Should().Contain("Nothing has been stopped")
            .And.Contain($"pid {RunningBefore.ProcessId}")
            .And.Contain("h9k daemon stop, then h9k doctor --yes, then h9k daemon start",
                "a failure before the stop signal has the whole hand recovery still ahead of it");
    }

    [Fact]
    public void A_failure_after_the_stop_signal_names_the_step_and_only_the_steps_not_yet_done()
    {
        IReadOnlyList<RestartStep> steps = DaemonRestartHandoff.PlanSteps();

        string message = DaemonRestartHandoff.DescribeFailedStep(steps, failedIndex: 1, RestartStepResult.Exited(ExitCodes.Error));

        message.Should().Contain("step 2 of 3")
            .And.Contain("h9k doctor --yes")
            .And.Contain("Still to do by hand")
            .And.Contain("h9k daemon start");
        message.Should().NotContain(
            "h9k daemon stop",
            "the stop already succeeded, and telling an operator to re-run it is telling them to undo the restart");
    }

    [Fact]
    public void A_failure_at_the_last_step_says_there_is_nothing_left_to_do_by_hand()
    {
        IReadOnlyList<RestartStep> steps = DaemonRestartHandoff.PlanSteps();

        string message = DaemonRestartHandoff.DescribeFailedStep(steps, failedIndex: 2, RestartStepResult.Exited(ExitCodes.Error));

        message.Should().Contain("step 3 of 3")
            .And.Contain("No step after it was planned")
            .And.Contain("h9k daemon status");
    }

    [Fact]
    public async Task A_later_step_that_never_launched_is_a_failure_after_the_stop_signal()
    {
        RecordingRunner runner = new(
            RestartStepResult.Exited(ExitCodes.Ok),
            RestartStepResult.Exited(ExitCodes.Ok),
            RestartStepResult.NotLaunched("the file is locked"));

        int exitCode = await DaemonRestartHandoff.RunAsync(
            Binary, RunningBefore, runner.RunAsync, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Error);
        runner.CommandLines.Should().HaveCount(3);

        string message = DaemonRestartHandoff.DescribeFailedStep(
            DaemonRestartHandoff.PlanSteps(), failedIndex: 2, RestartStepResult.NotLaunched("the file is locked"));
        message.Should().Contain("could not be launched: the file is locked");
    }
}
