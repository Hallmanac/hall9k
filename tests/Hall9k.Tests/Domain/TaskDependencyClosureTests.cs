using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
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
    private const string PullRequest = "https://github.com/x/y/pull/7";

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
        TaskDependency dependency = Dependency(
            TaskState.Done, runState, closedOut: false, PullRequest);

        dependency.Blocks.Should().BeTrue();
        dependency.IsDead.Should().BeTrue(
            "no run is left to observe a merge, so waiting on one is waiting forever");
        dependency.DescribeDeath().Should().Contain(runState).And.Contain("reads Done");
    }

    [Fact]
    public void A_done_dependency_names_the_lever_that_puts_its_pull_request_back_under_watch()
    {
        TaskDependency dependency = Dependency(
            TaskState.Done, RunState.Failed, closedOut: false, PullRequest);

        string death = dependency.DescribeDeath();
        death.Should().Contain("h9k pr resolve",
            "a follow-up run rejoins the closeout monitor's watch set, which is what observes the merge");
        death.Should().NotContain("Land its work",
            "the monitor only inspects runs still in the watch set, so a merge made on that advice "
            + "is never observed and the hold never lifts");
        death.Should().Contain("revise this task's dependencies");
    }

    [Fact]
    public void A_done_dependency_with_no_pull_request_offers_only_the_dependent_s_own_edges()
    {
        TaskDependency dependency = Dependency(TaskState.Done, RunState.Failed, closedOut: false);

        dependency.DescribeDeath().Should().NotContain("h9k pr resolve",
            "the reopen refuses a task with no pull request to follow up on - advice must be "
            + "self-correcting, not self-defeating");
        dependency.DescribeDeath().Should().Contain("revise this task's dependencies");
    }

    [Fact]
    public void A_done_dependency_with_no_run_at_all_will_never_close_out()
    {
        TaskDependency dependency = Dependency(TaskState.Done, currentRunState: null, closedOut: false);

        dependency.IsDead.Should().BeTrue();
        dependency.DescribeDeath().Should().Contain("no run left");
        dependency.DescribeDeath().Should().NotContain("h9k pr resolve",
            "the reopen refuses a task with no recorded run to follow up on");
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

    [Fact]
    public void A_done_pr_review_dependency_never_advises_pr_resolve()
    {
        TaskDependency dependency = Dependency(
            TaskState.Done, RunState.Failed, closedOut: false, PullRequest, TaskType.PrReview);

        string death = dependency.DescribeDeath();
        death.Should().NotContain("h9k pr resolve",
            "TaskDecider.Reopen refuses a pr-review task outright - its PullRequestUrl names the "
            + "pull request it reviewed, not one of its own to reopen, so advice must be "
            + "self-correcting, not self-defeating");
        death.Should().Contain("revise this task's dependencies");
    }

    /// <summary>
    /// Origin: independent pre-PR review, cycle 3. <c>h9k task resolve A --pr &lt;url&gt;</c>
    /// records a pull-request number onto A's own Failed run document
    /// (<c>PullRequestRecordedOnFailedRun</c>), which puts A's pull request back in
    /// <c>CloseoutEngine</c>'s orphan-sweep candidate set — the sweep is going to complete A's
    /// closeout unaided. A dependent waiting on A must not be told A is dead while that is true:
    /// the old rule declared every Failed run unreachable regardless, which stranded the
    /// dependent in Blocked reading "recover the blocker" while the sweep was already handling it.
    /// </summary>
    [Theory]
    [InlineData("Failed")]
    [InlineData("Killed")]
    public void A_done_dependency_the_orphan_sweep_is_still_watching_is_not_dead(string runState)
    {
        TaskDependency dependency = Dependency(
            TaskState.Done, runState, closedOut: false, PullRequest,
            runPullRequestNumber: 7);

        dependency.Blocks.Should().BeTrue("the merge has not been observed yet");
        dependency.IsDead.Should().BeFalse(
            "the orphan sweep still watches this pull request and will complete closeout unaided");
    }

    /// <summary>
    /// The orphan sweep itself excludes a run whose recorded failure is
    /// <c>RunDetails.PullRequestClosedWithoutMerge</c> — that run already told a prior inspection
    /// everything it could, and a repeat would only relearn it. A dependency carrying that same
    /// reason must read dead exactly as before, not newly "still watched".
    /// </summary>
    [Fact]
    public void A_done_dependency_whose_pull_request_closed_without_merging_is_still_dead()
    {
        TaskDependency dependency = Dependency(
            TaskState.Done, RunState.Failed, closedOut: false, PullRequest,
            runPullRequestNumber: 7, runFailureReason: RunDetails.PullRequestClosedWithoutMerge);

        dependency.IsDead.Should().BeTrue(
            "the sweep already excludes this run — nothing is left to watch it any more");
    }

    /// <summary>
    /// The sweep never watches Superseded (<c>CloseoutEngine.PollOnceAsync</c>'s own orphan
    /// query names only Failed and Killed), so a superseded run stays dead even with a recorded
    /// pull-request number — the orphan-sweep exception must not widen past the sweep's own reach.
    /// </summary>
    [Fact]
    public void A_superseded_dependency_with_a_recorded_pull_request_is_still_dead()
    {
        TaskDependency dependency = Dependency(
            TaskState.Done, RunState.Superseded, closedOut: false, PullRequest,
            runPullRequestNumber: 7);

        dependency.IsDead.Should().BeTrue("the orphan sweep only ever watches Failed or Killed runs");
    }

    private static TaskDependency Dependency(
        TaskState state, RunState? currentRunState, bool closedOut, string? pullRequestUrl = null,
        TaskType? type = null, int? runPullRequestNumber = null, string? runFailureReason = null) =>
        new(DomainId.New(), "A blocker", state, closedOut, currentRunState, pullRequestUrl,
            type ?? TaskType.Chore, [], runPullRequestNumber, runFailureReason);
}
