using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Which blockers can still reach true closeout and which can not (Decisions Log #34). Getting
/// this wrong in the "can not" direction is the expensive one: a dependent left Blocked behind
/// a dependency that will never close out waits forever, reads as ordinary waiting, and shows
/// no reason. Origin incident (2026-08-20): the rule enumerated Failed and Abandoned only, so
/// h9k task resolve — which ends a task Done on a run that already failed — silently stranded
/// every dependent behind it.
/// </summary>
public sealed class TaskDependencyClosureTests
{
    [Fact]
    public void A_done_dependency_whose_run_the_monitor_completed_has_closed_out()
    {
        TaskDependency dependency = Dependency(TaskState.Done, RunState.Completed, closedOut: true);

        dependency.Blocks.Should().BeFalse();
        dependency.IsDead.Should().BeFalse("closed out is the opposite of dead, not a flavour of it");
    }

    [Theory]
    [InlineData("Dispatched")]
    [InlineData("Running")]
    [InlineData("AwaitingReview")]
    [InlineData("ChecksFailing")]
    [InlineData("ReviewPending")]
    [InlineData("CloseoutParked")]
    public void A_dependency_whose_run_is_still_in_the_pipeline_blocks_without_being_dead(string runState)
    {
        TaskDependency dependency = Dependency(TaskState.Done, runState, closedOut: false);

        dependency.Blocks.Should().BeTrue("the merge has not been observed yet");
        dependency.IsDead.Should().BeFalse("the run carrying the pull request can still get there");
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Killed")]
    [InlineData("Superseded")]
    public void A_done_dependency_whose_run_ended_without_a_merge_will_never_close_out(string runState)
    {
        TaskDependency dependency = Dependency(TaskState.Done, runState, closedOut: false);

        dependency.Blocks.Should().BeTrue();
        dependency.IsDead.Should().BeTrue(
            "no run is left to observe a merge, so waiting on one is waiting forever");
        dependency.DescribeDeath().Should().Contain(runState).And.Contain("reads Done");
    }

    [Fact]
    public void A_done_dependency_with_no_run_at_all_will_never_close_out()
    {
        TaskDependency dependency = Dependency(TaskState.Done, currentRunState: null, closedOut: false);

        dependency.IsDead.Should().BeTrue();
        dependency.DescribeDeath().Should().Contain("no run left");
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void A_dependency_that_ended_without_closing_out_is_dead_whatever_its_run_says(string taskState)
    {
        TaskDependency dependency = Dependency(taskState, RunState.Running, closedOut: false);

        dependency.IsDead.Should().BeTrue();
        dependency.DescribeDeath().Should().Contain("will never close out on its own");
    }

    [Fact]
    public void A_failed_dependency_offers_the_levers_the_decider_will_actually_accept()
    {
        TaskDependency dependency = Dependency("Failed", RunState.Failed, closedOut: false);

        dependency.DescribeDeath().Should().Contain("Retry or resolve it",
            "Failed still has exits, and the message may advertise them");
    }

    [Fact]
    public void An_abandoned_dependency_never_advises_retry_or_resolve()
    {
        TaskDependency dependency = Dependency("Abandoned", RunState.Failed, closedOut: false);

        string death = dependency.DescribeDeath();
        death.Should().NotContain("Retry or resolve",
            "the decider refuses both levers on Abandoned - advice must be self-correcting, not self-defeating");
        death.Should().Contain("dead end by design");
        death.Should().Contain("revise this task's dependencies",
            "the dependent's own edges are the only honest remedy");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Published")]
    [InlineData("Queued")]
    [InlineData("Blocked")]
    [InlineData("Claimed")]
    [InlineData("NeedsHuman")]
    public void A_dependency_still_working_its_way_through_the_lifecycle_blocks_without_being_dead(string taskState)
    {
        TaskDependency dependency = Dependency(taskState, currentRunState: null, closedOut: false);

        dependency.Blocks.Should().BeTrue();
        dependency.IsDead.Should().BeFalse("it has not ended — it simply has not got there yet");
    }

    private static TaskDependency Dependency(TaskState state, RunState? currentRunState, bool closedOut) =>
        new(DomainId.New(), "A blocker", state, closedOut, currentRunState, []);
}
