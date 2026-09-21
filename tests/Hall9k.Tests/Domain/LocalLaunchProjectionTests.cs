using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A local launch's whole life on the run's projection (idea b9b09779, piece 5): started, paused
/// at the human step, resumed, up, stopped. DB-free — the projection is driven straight through
/// its Apply methods with a fake event, which is what makes the cursor arithmetic that
/// <c>--continue</c> depends on assertable without a store.
/// </summary>
public sealed class LocalLaunchProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid LaunchId = Guid.NewGuid();
    private static readonly Guid TaskId = Guid.NewGuid();
    private static readonly Guid NodeId = Guid.NewGuid();

    /// <summary>The <c>h9k</c> process walking the plan, as a start or a resume records it.</summary>
    private static readonly LocalLaunchProcess Walker = new(1717, At, "h9k task run-local 09fad63d");

    /// <summary>The plan the walker tests use, cut down to the four positions this projection cares about.</summary>
    private static readonly IReadOnlyList<RunSkillStep> Steps =
    [
        new RunSkillStep(1, RunSkillStepKind.Human, RunSkillDocument.HumanStepsHeading, "Get the API key", string.Empty),
        new RunSkillStep(2, RunSkillStepKind.Command, RunSkillDocument.PrerequisitesHeading, "Check", "dotnet --version"),
        new RunSkillStep(3, RunSkillStepKind.Command, RunSkillDocument.OneTimeSetupHeading, "Restore", "dotnet restore"),
        new RunSkillStep(4, RunSkillStepKind.Command, RunSkillDocument.LaunchHeading, "Start", "make dev"),
    ];

    [Fact]
    public void A_started_launch_is_live_at_step_one_with_nothing_running()
    {
        RunDetails run = Started();

        run.LocalLaunch.Should().NotBeNull();
        run.LocalLaunch?.Live.Should().BeTrue();
        run.LocalLaunch?.NextStepNumber.Should().Be(1);
        run.LocalLaunch?.Walked.Should().BeFalse();
        run.LocalLaunch?.Processes.Should().BeEmpty();
        run.LocalLaunch?.NodeId.Should().Be(NodeId);
        run.LocalLaunch?.TaskId.Should().Be(TaskId);
    }

    [Fact]
    public void Pausing_then_resuming_moves_the_cursor_past_the_human_step()
    {
        RunDetails run = Started();

        Apply(run, new LocalLaunchPausedForHuman(RunId, LaunchId, 1, "Get the API key", At));
        run.LocalLaunch?.AwaitingHuman.Should().BeTrue();
        run.LocalLaunch?.NextStepNumber.Should().Be(1);

        Apply(run, new LocalLaunchResumed(RunId, LaunchId, 2, Walker, At, Guid.NewGuid()));
        run.LocalLaunch?.AwaitingHuman.Should().BeFalse();
        run.LocalLaunch?.AwaitingHumanAtStep.Should().BeNull();
        run.LocalLaunch?.NextStepNumber.Should().Be(2);
    }

    /// <summary>
    /// The walker is whoever is mid-walk right now, and no longer than that. A pass that paused or
    /// finished clears it, so the next reader cannot take a pid that has already exited for a walk
    /// still in flight — which is the one signal separating "started nothing yet" from "everything
    /// it started is gone" (<see cref="LocalLaunchAdmission.ForStart"/>).
    /// </summary>
    [Fact]
    public void The_walker_is_recorded_while_a_pass_walks_and_cleared_once_it_ends()
    {
        RunDetails run = Started();
        run.LocalLaunch?.Walker.Should().Be(Walker);

        Apply(run, new LocalLaunchPausedForHuman(RunId, LaunchId, 1, "Get the API key", At));
        run.LocalLaunch?.Walker.Should().BeNull();

        Apply(run, new LocalLaunchResumed(RunId, LaunchId, 2, Walker, At, Guid.NewGuid()));
        run.LocalLaunch?.Walker.Should().Be(Walker);

        Apply(run, new LocalLaunchRunning(RunId, LaunchId, [], null, string.Empty, Steps.Count + 1, At));
        run.LocalLaunch?.Walker.Should().BeNull();
    }

    [Fact]
    public void A_walked_launch_carries_its_processes_its_port_and_its_address()
    {
        RunDetails run = Started();

        Apply(run, new LocalLaunchRunning(
            RunId, LaunchId, [new LocalLaunchProcess(4242, At, "make dev")], 54321,
            "http://localhost:54321/swagger", Steps.Count + 1, At));

        run.LocalLaunch?.Walked.Should().BeTrue();
        run.LocalLaunch?.Port.Should().Be(54321);
        run.LocalLaunch?.Address.Should().Be("http://localhost:54321/swagger");
        run.LocalLaunch?.Processes.Should().ContainSingle().Which.ProcessId.Should().Be(4242);
    }

    [Fact]
    public void Stopping_ends_the_launch_and_clears_what_it_was_running()
    {
        RunDetails run = Started();
        Apply(run, new LocalLaunchRunning(
            RunId, LaunchId, [new LocalLaunchProcess(4242, At, "make dev")], 54321, "http://localhost:54321",
            Steps.Count + 1, At));

        Apply(run, new LocalLaunchStopped(RunId, LaunchId, LocalLaunchStopReason.TaskClosedOut, [4242], At));

        run.LocalLaunch?.Live.Should().BeFalse();
        run.LocalLaunch?.StoppedReason.Should().Be(LocalLaunchStopReason.TaskClosedOut);
        run.LocalLaunch?.Processes.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_step_ends_the_launch_and_keeps_what_the_step_said()
    {
        RunDetails run = Started();

        Apply(run, new LocalLaunchFailed(
            RunId, LaunchId, 3, "dotnet restore", "exit code 1: MSB1003", [], At));

        run.LocalLaunch?.Live.Should().BeFalse();
        run.LocalLaunch?.FailedAtStep.Should().Be(3);
        run.LocalLaunch?.FailedReason.Should().Contain("MSB1003");
    }

    /// <summary>
    /// An event for a launch this run has already moved past — a stop landing after a fresh launch
    /// started — must not move the live launch's state on a fact observed about a dead one.
    /// </summary>
    [Fact]
    public void An_event_naming_another_launch_leaves_the_live_one_alone()
    {
        RunDetails run = Started();

        Apply(run, new LocalLaunchStopped(RunId, Guid.NewGuid(), LocalLaunchStopReason.Requested, [], At));

        run.LocalLaunch?.Live.Should().BeTrue();
        run.LocalLaunch?.StoppedReason.Should().BeNull();
    }

    private static RunDetails Started()
    {
        RunDetails run = new() { Id = RunId };
        Apply(run, new LocalLaunchStarted(
            RunId, LaunchId, TaskId, NodeId, "/tmp/worktree", Steps, Walker, At, Guid.NewGuid()));
        return run;
    }

    private static void Apply<T>(RunDetails run, T @event)
        where T : notnull
    {
        RunDetailsProjection projection = new();
        switch (@event)
        {
            case LocalLaunchStarted started:
                projection.Apply(new FakeEvent<LocalLaunchStarted>(started), run);
                break;
            case LocalLaunchPausedForHuman paused:
                projection.Apply(new FakeEvent<LocalLaunchPausedForHuman>(paused), run);
                break;
            case LocalLaunchResumed resumed:
                projection.Apply(new FakeEvent<LocalLaunchResumed>(resumed), run);
                break;
            case LocalLaunchRunning running:
                projection.Apply(new FakeEvent<LocalLaunchRunning>(running), run);
                break;
            case LocalLaunchFailed failed:
                projection.Apply(new FakeEvent<LocalLaunchFailed>(failed), run);
                break;
            case LocalLaunchStopped stopped:
                projection.Apply(new FakeEvent<LocalLaunchStopped>(stopped), run);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(@event), @event, "No launch event of that type.");
        }
    }
}
