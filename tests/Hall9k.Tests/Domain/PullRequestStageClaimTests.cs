using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>h9k task drive</c> on a task whose pull request is open, decided and folded without a store: the
/// reopen and the interactive claim recorded together, what the aggregate says afterwards, every state
/// that is refused, and the release that gives the pull request back to closeout.
/// </summary>
public sealed class PullRequestStageClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("01a18010-0000-7000-8000-000000000001");
    private static readonly Guid NodeId = Guid.Parse("01a18010-0000-7000-8000-000000000002");
    private const string PullRequestUrl = "https://github.com/acme/web/pull/42";
    private const string Branch = "task/abc12345-thing";

    /// <summary>A task Done with its pull request open, completed by <see cref="PullRequestRunId"/>.</summary>
    private static TaskAggregate DoneWithPullRequest(out Guid pullRequestRunId, IReadOnlyList<Guid>? blockedBy = null)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "Bound the limiter", ["it resets per window"], TaskType.Feature, null, null, null,
            Now, Owner, blockedBy: blockedBy);
        TaskAggregate task = new();
        task.Apply(added);
        task.Apply(TaskDecider.Publish(task, GraphFor(blockedBy), Now, Owner));
        TaskAssigned assigned = TaskDecider.Assign(task, Owner, DependenciesFor(blockedBy), Now, Owner);
        task.Apply(assigned);

        pullRequestRunId = DomainId.New();
        TaskClaimed claimed = assigned.UnmetDependencies.Count > 0
            ? TaskDecider.ClaimDeliberately(task, Owner, pullRequestRunId, Now, dependencyOverrideAcknowledged: true)
            : TaskDecider.Claim(task, NodeId, Owner, pullRequestRunId, Now);
        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, pullRequestRunId, PullRequestUrl, Now));
        return task;
    }

    private static TaskDependencyGraph GraphFor(IReadOnlyList<Guid>? blockedBy) =>
        blockedBy is { Count: > 0 } ? new TaskDependencyGraph(DependenciesFor(blockedBy)) : TaskDependencyGraph.Empty;

    private static IReadOnlyList<TaskDependency> DependenciesFor(IReadOnlyList<Guid>? blockedBy) =>
        [.. (blockedBy ?? []).Select(id => new TaskDependency(
            id, "The blocker", TaskState.Claimed, false, RunState.Running, null, TaskType.Feature, []))];

    private static (TaskReopened? Reopened, TaskClaimed Claimed) Claim(
        TaskAggregate task, Guid pullRequestRunId, bool open = true, string? live = null, bool acknowledge = false) =>
        TaskDecider.ClaimPullRequestInteractively(
            task, pullRequestRunId, Branch, open, live, DomainId.New(), Now, Owner, acknowledge);

    [Fact]
    public void A_done_task_with_an_open_pull_request_records_a_manual_reopen_and_an_interactive_claim()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);

        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);

        reopened.Should().NotBeNull();
        reopened!.Automatic.Should().BeFalse("a human's own reopen");
        reopened.Branch.Should().Be(Branch);
        reopened.PreviousRunId.Should().Be(pullRequestRunId);
        reopened.Kind.Should().Be(FollowUpKind.ReviewFeedback);
        reopened.Reason.Should().Be(TaskDecider.PullRequestStageDriveReason);
        claimed.NodeId.Should().Be(Guid.Empty, "the interactive sentinel");
        claimed.InteractiveMode.Should().BeTrue();
    }

    [Fact]
    public void Folded_together_the_events_leave_the_task_claimed_on_the_pull_requests_branch_with_its_run_remembered()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);
        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);

        task.Apply(reopened!);
        task.Apply(claimed);

        task.State.Should().Be(TaskState.Claimed);
        task.IsInteractiveClaim.Should().BeTrue();
        task.InteractiveModeEnabled.Should().BeTrue();
        task.FollowUpBranch.Should().Be(Branch);
        task.PullRequestUrl.Should().Be(PullRequestUrl, "the existing pull request is kept");
        task.CurrentRunId.Should().Be(claimed.RunId);
        task.PullRequestRunId.Should().Be(pullRequestRunId, "closeout tells the pull request's run apart from the claim's by this");
    }

    [Fact]
    public void A_pull_request_that_merged_or_closed_has_nothing_left_to_iterate_on()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);

        Action act = () => Claim(task, pullRequestRunId, open: false);

        act.Should().Throw<DomainConflictException>().WithMessage("*merged or been closed*");
    }

    [Fact]
    public void A_live_headless_follow_up_session_refuses_the_claim_and_is_named()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);

        Action act = () => Claim(task, pullRequestRunId, live: "abc12345-build");

        act.Should().Throw<DomainConflictException>().WithMessage("*headless follow-up session*abc12345-build*");
    }

    [Fact]
    public void A_live_headless_session_refuses_a_reopened_lap_too()
    {
        TaskAggregate task = ReopenedLap(out Guid pullRequestRunId, FollowUpKind.FailingChecks);

        Action act = () => Claim(task, pullRequestRunId, live: "abc12345-build");

        act.Should().Throw<DomainConflictException>().WithMessage("*abc12345-build*");
    }

    [Fact]
    public void A_task_a_node_has_claimed_for_a_follow_up_lap_is_refused()
    {
        TaskAggregate task = ReopenedLap(out Guid pullRequestRunId, FollowUpKind.FailingChecks);
        task.Apply(TaskDecider.Claim(task, NodeId, Owner, DomainId.New(), Now));

        Action act = () => Claim(task, pullRequestRunId);

        act.Should().Throw<DomainConflictException>().WithMessage("*claimed by a node running a follow-up lap*");
    }

    [Fact]
    public void A_queued_task_that_was_never_reopened_is_refused()
    {
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Not yet built", ["it works"], TaskType.Chore, null, null, null, Now, Owner);
        (TaskAggregate queued, _) = TaskSeed.Start(added, Owner, Now);
        task = queued;

        Action act = () => Claim(task, DomainId.New());

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void A_pr_review_task_is_refused()
    {
        Guid taskId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Review someone else's change", ["a report"], TaskType.PrReview, null, null, null, Now, Owner));

        Action act = () => Claim(task, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*pull-request review*");
    }

    [Fact]
    public void A_task_assigned_to_another_owner_is_refused()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);

        Action act = () => TaskDecider.ClaimPullRequestInteractively(
            task, pullRequestRunId, Branch, true, null, DomainId.New(), Now, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*claims only their own owner's work*");
    }

    [Fact]
    public void From_a_reopened_queued_lap_only_the_claim_is_recorded_and_the_laps_context_carries_into_the_session()
    {
        TaskAggregate task = ReopenedLap(out Guid pullRequestRunId, FollowUpKind.FailingChecks);
        ReviewReadScope scope = new(
            [new ReadableReviewThread("PRRT_1", "alice", FeedbackAdmission.Member, ["PRRC_1"])], ["PRRT_2"]);
        ReviewThreadReference thread = new("PRRT_1", "alice", "https://github.com/acme/web/pull/42#discussion_r1");
        task = ReopenedLap(out pullRequestRunId, FollowUpKind.FailingChecks, scope, [thread]);

        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);
        reopened.Should().BeNull("the existing reopen stands");
        task.Apply(claimed);

        task.State.Should().Be(TaskState.Claimed);
        task.IsInteractiveClaim.Should().BeTrue();
        task.FollowUpKind.Should().Be(FollowUpKind.FailingChecks, "the lap's kind is not overwritten");
        task.ReviewReadScope.Should().Be(scope);
        task.KnownHumanReviewThreads.Should().ContainSingle().Which.ThreadId.Should().Be("PRRT_1");
        task.FollowUpBranch.Should().Be(Branch);
    }

    [Fact]
    public void A_reopened_lap_that_landed_blocked_behind_an_open_dependency_needs_the_acknowledgment_and_then_claims_only()
    {
        Guid blocker = DomainId.New();
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId, [blocker]);
        TaskReopened reopened = TaskDecider.Reopen(
            task, pullRequestRunId, Branch, "CI failing", FollowUpKind.FailingChecks, automatic: true, Now, Owner);
        task.Apply(reopened);
        task.State.Should().Be(TaskState.Blocked);

        // The deliberate claim that put the task here already acknowledged this blocker, so it carries forward.
        (TaskReopened? again, TaskClaimed claimed) = Claim(task, pullRequestRunId);

        again.Should().BeNull();
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeTrue();
        claimed.InteractiveMode.Should().BeTrue();
    }

    [Fact]
    public void Release_returns_the_task_to_done_with_its_pull_request_and_makes_the_pull_requests_run_current_again()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);
        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);
        task.Apply(reopened!);
        task.Apply(claimed);

        InteractiveClaimReturnedToPullRequest returned = TaskDecider.ReleaseInteractiveClaimToPullRequest(task, Now);
        task.Apply(returned);

        returned.ClaimRunId.Should().Be(claimed.RunId);
        returned.PullRequestRunId.Should().Be(pullRequestRunId);
        task.State.Should().Be(TaskState.Done);
        task.CurrentRunId.Should().Be(pullRequestRunId, "closeout's ordinary watch resumes");
        task.PullRequestUrl.Should().Be(PullRequestUrl);
        task.FollowUpBranch.Should().BeNull();
        task.IsInteractiveClaim.Should().BeFalse();
        task.InteractiveModeEnabled.Should().BeFalse("the flag clears by default");
    }

    [Fact]
    public void Release_keeps_the_interactive_flag_when_asked_to()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);
        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);
        task.Apply(reopened!);
        task.Apply(claimed);

        task.Apply(TaskDecider.ReleaseInteractiveClaimToPullRequest(task, Now, keepInteractive: true));

        task.State.Should().Be(TaskState.Done);
        task.InteractiveModeEnabled.Should().BeTrue();
    }

    [Fact]
    public void Release_to_the_pull_request_is_refused_without_an_interactive_claim()
    {
        TaskAggregate task = DoneWithPullRequest(out _);

        Action act = () => TaskDecider.ReleaseInteractiveClaimToPullRequest(task, Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void A_handback_after_the_claim_queues_a_headless_follow_up_lap_on_the_pull_requests_branch_and_clears_the_flag()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);
        (TaskReopened? reopened, TaskClaimed claimed) = Claim(task, pullRequestRunId);
        task.Apply(reopened!);
        task.Apply(claimed);

        task.Apply(TaskDecider.HandBack(task, claimed.RunId, Branch, "automation finishes", Now, Owner));

        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be(Branch, "the dispatcher's launcher checks out the follow-up branch");
        task.PullRequestUrl.Should().Be(PullRequestUrl);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewFeedback);
        task.InteractiveModeEnabled.Should().BeFalse();
    }

    [Fact]
    public void A_completion_that_carries_a_pull_request_names_the_run_closeout_watches_it_through()
    {
        TaskAggregate task = DoneWithPullRequest(out Guid pullRequestRunId);

        task.PullRequestRunId.Should().Be(pullRequestRunId);
    }

    private static TaskAggregate ReopenedLap(
        out Guid pullRequestRunId, FollowUpKind kind, ReviewReadScope? scope = null,
        IReadOnlyList<ReviewThreadReference>? threads = null)
    {
        TaskAggregate task = DoneWithPullRequest(out pullRequestRunId);
        task.Apply(TaskDecider.Reopen(
            task, pullRequestRunId, Branch, "CI failing", kind, automatic: true, Now, Owner,
            humanReviewThreads: threads, reviewReadScope: scope));
        return task;
    }
}
