using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// "Refuses when another launch of the same task is already up" (idea b9b09779, piece 5), as a
/// decision rather than a throw, so the rule is pinned without a store or a process.
/// </summary>
public sealed class LocalLaunchAdmissionTests
{
    private static readonly Guid TaskId = Guid.NewGuid();
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The <c>h9k</c> process walking the plan, told apart from the launch's own processes so one predicate can answer for both.</summary>
    private static readonly LocalLaunchProcess WalkerProcess = new(1717, StartedAt, "h9k task run-local 09fad63d");

    [Fact]
    public void A_run_with_no_launch_recorded_admits_one()
    {
        LocalLaunchAdmission.ForStart(null, TaskId, _ => true).Should().Be(LocalLaunchAdmission.Clear);
    }

    [Fact]
    public void A_second_launch_is_refused_while_the_first_is_running()
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(Running(), TaskId, _ => true);

        admission.Refusal.Should().Contain($"A local launch of task {TaskId} is already up");
        admission.Refusal.Should().Contain("1 of its process(es) are still running");
        admission.Refusal.Should().Contain("--stop");
        admission.ClearStaleLaunch.Should().BeFalse();
    }

    /// <summary>
    /// A launch waiting on a human has started nothing and is still somebody's live walk of this
    /// branch. Admitting a second over the same checkout would be two launches racing for the same
    /// ports and the same files.
    /// </summary>
    [Fact]
    public void A_second_launch_is_refused_while_the_first_waits_on_a_human()
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(Paused(), TaskId, _ => false);

        admission.Refusal.Should().Contain("it is waiting on you at step 1");
        admission.Refusal.Should().Contain("--continue");
        admission.ClearStaleLaunch.Should().BeFalse();
    }

    /// <summary>
    /// A launch part-way through a one-time setup step that takes minutes has recorded no
    /// processes yet, and that is not the same fact as everything it started being gone. Admitting
    /// a second here would walk the same checkout alongside the first and leave the first
    /// launch's server recorded nowhere, because the projection drops a running event naming a
    /// launch the view has moved past.
    /// </summary>
    [Fact]
    public void A_second_launch_is_refused_while_the_first_is_still_walking()
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(Walking(), TaskId, IsWalker);

        admission.Refusal.Should().Contain("it is still walking this project's run skill at step 1");
        admission.Refusal.Should().Contain("--stop");
        admission.ClearStaleLaunch.Should().BeFalse();
    }

    /// <summary>
    /// A record still reading live whose processes are all gone is not a launch that is up, so the
    /// new one proceeds — and the old record is closed out rather than left to refuse every future
    /// launch of this task.
    /// </summary>
    [Fact]
    public void A_launch_whose_processes_are_gone_is_cleared_rather_than_refused_over()
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(Running(), TaskId, _ => false);

        admission.Refusal.Should().BeNull();
        admission.ClearStaleLaunch.Should().BeTrue();
    }

    /// <summary>
    /// The other half of the same distinction: a walk whose own <c>h9k</c> process died part-way
    /// through, having started nothing, is over however live the record reads, so it is cleared
    /// rather than left refusing every future launch of this task forever.
    /// </summary>
    [Fact]
    public void A_launch_whose_walk_died_part_way_through_is_cleared_rather_than_refused_over()
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(Walking(), TaskId, _ => false);

        admission.Refusal.Should().BeNull();
        admission.ClearStaleLaunch.Should().BeTrue();
    }

    [Fact]
    public void An_already_stopped_launch_is_in_nobodys_way()
    {
        LocalLaunchState stopped = Running() with
        {
            StoppedReason = LocalLaunchStopReason.Requested,
            StoppedAt = StartedAt,
        };

        LocalLaunchAdmission.ForStart(stopped, TaskId, _ => true).Should().Be(LocalLaunchAdmission.Clear);
    }

    private static LocalLaunchState Running() =>
        Base() with { Processes = [new LocalLaunchProcess(4242, StartedAt, "make dev")], NextStepNumber = 2 };

    private static LocalLaunchState Paused() => Base() with { AwaitingHumanAtStep = 1 };

    /// <summary>A launch whose first step is still running: a walker recorded, and nothing started yet.</summary>
    private static LocalLaunchState Walking() => Base() with { Walker = WalkerProcess };

    private static bool IsWalker(LocalLaunchProcess process) => process == WalkerProcess;

    private static LocalLaunchState Base() =>
        new(
            Guid.NewGuid(), TaskId, Guid.NewGuid(), "/tmp/worktree",
            [new RunSkillStep(1, RunSkillStepKind.Human, RunSkillDocument.HumanStepsHeading, "Log in", string.Empty)],
            NextStepNumber: 1, AwaitingHumanAtStep: null, Processes: [], Walker: null, Port: null,
            Address: string.Empty,
            StartedAt, StoppedReason: null, StoppedAt: null, FailedAtStep: null, FailedReason: null);
}
