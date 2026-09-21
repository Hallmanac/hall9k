using FluentAssertions;
using Hall9k.Daemon.LocalLaunches;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// "Never left running" (idea b9b09779, piece 5), as a decision over four observations. DB-free
/// and process-free.
/// </summary>
public sealed class LocalLaunchTeardownTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_live_launch_on_a_live_task_in_a_live_worktree_is_left_alone()
    {
        Reason(Running(), TaskState.Claimed, worktreeExists: true, alive: true).Should().BeNull();
    }

    /// <summary>The criterion's own case: the task closes out and the launch comes down with it.</summary>
    [Fact]
    public void A_task_that_closed_out_takes_its_launch_down()
    {
        foreach (TaskState terminal in new[] { TaskState.Done, TaskState.Abandoned })
        {
            Reason(Running(), terminal, worktreeExists: true, alive: true)
                .Should().Be(LocalLaunchStopReason.TaskClosedOut, "{0} is terminal", terminal.Value);
        }
    }

    [Fact]
    public void A_removed_worktree_takes_its_launch_down()
    {
        Reason(Running(), TaskState.NeedsHuman, worktreeExists: false, alive: true)
            .Should().Be(LocalLaunchStopReason.WorktreeRemoved);
    }

    /// <summary>
    /// The process died on its own. Recorded as gone rather than as a stop the platform made, and
    /// never left reading as live, which would refuse the reviewer's next launch of the same task.
    /// </summary>
    [Fact]
    public void A_launch_whose_process_is_already_gone_is_closed_out()
    {
        Reason(Running(), TaskState.NeedsHuman, worktreeExists: true, alive: false)
            .Should().Be(LocalLaunchStopReason.ProcessGone);
    }

    /// <summary>
    /// A launch waiting on a human has started nothing, which is not the same fact as everything it
    /// started being gone. Reading the first as the second would tear down the walk the reviewer is
    /// in the middle of.
    /// </summary>
    [Fact]
    public void A_launch_paused_at_a_human_step_is_not_read_as_a_dead_one()
    {
        Reason(Paused(), TaskState.NeedsHuman, worktreeExists: true, alive: false).Should().BeNull();
    }

    /// <summary>
    /// A task document this node has not replicated yet is an absence, not an observation that the
    /// work is over, and acting on it would kill a live walk over a document that has not arrived.
    /// </summary>
    [Fact]
    public void An_unreadable_task_is_no_answer_rather_than_a_closed_one()
    {
        Reason(Running(), taskState: null, worktreeExists: true, alive: true).Should().BeNull();
    }

    [Fact]
    public void A_launch_that_already_ended_is_never_torn_down_twice()
    {
        LocalLaunchState stopped = Running() with
        {
            StoppedReason = LocalLaunchStopReason.Requested,
            StoppedAt = StartedAt,
        };

        Reason(stopped, TaskState.Done, worktreeExists: false, alive: false).Should().BeNull();
    }

    private static LocalLaunchStopReason? Reason(
        LocalLaunchState launch, TaskState? taskState, bool worktreeExists, bool alive) =>
        LocalLaunchTeardown.Reason(launch, taskState, worktreeExists, _ => alive);

    private static LocalLaunchState Running() =>
        Base() with
        {
            NextStepNumber = 2,
            Processes = [new LocalLaunchProcess(4242, StartedAt, "make dev")],
            Port = 54321,
            Address = "http://localhost:54321",
        };

    private static LocalLaunchState Paused() => Base() with { AwaitingHumanAtStep = 1, NextStepNumber = 1 };

    private static LocalLaunchState Base() =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/tmp/worktree",
            [new RunSkillStep(1, RunSkillStepKind.Human, RunSkillDocument.HumanStepsHeading, "Log in", string.Empty)],
            NextStepNumber: 1, AwaitingHumanAtStep: null, Processes: [], Walker: null, Port: null,
            Address: string.Empty,
            StartedAt, StoppedReason: null, StoppedAt: null, FailedAtStep: null, FailedReason: null);
}
