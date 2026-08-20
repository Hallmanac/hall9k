using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class TaskDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_without_objective_fails_the_readiness_contract()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: " ",
            acceptanceCriteria: ["it works"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*objective*");
    }

    /// <summary>
    /// The task override is the most specific link in the model chain (Decisions Log #33).
    /// It is vetted here because the value ends up on the executor's /bin/sh command line.
    /// </summary>
    [Fact]
    public void Add_canonicalizes_a_model_override_and_leaves_it_unknown_when_unstated()
    {
        TaskAdded withOverride = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Pin the model",
            acceptanceCriteria: ["it is recorded"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: " OPUS ");

        withOverride.Model.Should().Be(AgentModel.Opus);

        TaskAdded withoutOverride = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Let the chain decide",
            acceptanceCriteria: ["it is recorded"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        withoutOverride.Model.Should().Be(
            AgentModel.Unknown, "an unstated override defers to the role, project, and platform levels");
    }

    /// <summary>
    /// The word 'default' passes the shell charset check, so nothing but the value object
    /// stops it becoming a task override that spawns on the owner's personal setting.
    /// </summary>
    [Fact]
    public void Add_treats_a_model_of_default_as_no_override_rather_than_as_a_model_name()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Leave the model to the chain",
            acceptanceCriteria: ["It defers"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: " Default ");

        added.Model.Should().Be(
            AgentModel.Unknown, "'default' states no preference; it is never the model a session ran on");
    }

    [Fact]
    public void Add_rejects_a_model_that_could_not_be_handed_to_the_executors_shell()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Smuggle a command",
            acceptanceCriteria: ["it is refused"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: "opus; rm -rf /");

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }

    /// <summary>
    /// Creation is identity, not readiness (Decisions Log #34): a draft exists in order to
    /// gather criteria, so demanding them at Add would put the gate in the wrong place. The
    /// gate is Publish, and the test below is the other half of this one.
    /// </summary>
    [Fact]
    public void Add_without_acceptance_criteria_produces_a_draft_rather_than_refusing()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Add rate limiting to auth endpoints",
            acceptanceCriteria: [" ", ""], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        added.AcceptanceCriteria.Should().BeEmpty("blank criteria are no criteria");
        added.StartsAsDraft.Should().BeTrue("every task h9k creates now starts as a draft");

        TaskAggregate task = new();
        task.Apply(added);
        task.State.Should().Be(TaskState.Draft);
    }

    [Fact]
    public void Publish_without_acceptance_criteria_fails_the_readiness_contract()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Add rate limiting to auth endpoints",
            acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*acceptance criter*");
    }

    [Fact]
    public void Claim_increments_generation_and_carries_the_minted_run_id()
    {
        TaskAggregate task = QueuedTask();
        Guid runId = DomainId.New();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, runId, Now);

        claimed.LeaseGeneration.Should().Be(1);
        claimed.RunId.Should().Be(runId);

        task.Apply(claimed);
        task.State.Should().Be(TaskState.Claimed);
        task.CurrentRunId.Should().Be(runId);
        task.RunIds.Should().ContainSingle();
    }

    [Fact]
    public void Claim_of_a_claimed_task_conflicts()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Requeue_after_claim_returns_to_queued_and_a_reclaim_bumps_generation_again()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now));
        task.State.Should().Be(TaskState.Queued);

        TaskClaimed second = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        second.LeaseGeneration.Should().Be(2, "every claim increments the fencing token");
    }

    [Fact]
    public void Ask_then_answer_walks_needs_human_and_back()
    {
        TaskAggregate task = ClaimedTask();
        Guid questionId = DomainId.New();

        task.Apply(TaskDecider.Ask(task, questionId, task.CurrentRunId!.Value, "Which config wins?", Now));
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PendingQuestionId.Should().Be(questionId);

        task.Apply(TaskDecider.Answer(task, questionId, "The env var wins.", Now, DomainId.New()));
        task.State.Should().Be(TaskState.Claimed);
        task.PendingQuestionId.Should().BeNull();
    }

    [Fact]
    public void Answer_to_the_wrong_question_conflicts()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Ask(task, DomainId.New(), task.CurrentRunId!.Value, "A question", Now));

        Action act = () => TaskDecider.Answer(task, DomainId.New(), "answer", Now, DomainId.New());

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Complete_reaches_done_and_terminal_states_reject_further_decisions()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/1", Now));

        task.State.Should().Be(TaskState.Done);
        task.State.IsTerminal.Should().BeTrue();

        Action act = () => TaskDecider.Abandon(task, "changed my mind", Now, DomainId.New());
        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Reopen_of_done_task_queues_a_follow_up_on_the_pull_request_branch()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        Guid previousRunId = task.CurrentRunId!.Value;

        TaskReopened reopened = TaskDecider.Reopen(
            task, previousRunId, "task/abc-branch", "Unresolved review comments",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());
        task.Apply(reopened);

        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be("task/abc-branch");
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7", "the follow-up updates the existing PR");
        task.ClaimedByNodeId.Should().BeNull("a reopened task is queued, and queued work is unclaimed");
        task.CurrentRunId.Should().BeNull();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        claimed.LeaseGeneration.Should().Be(2, "a follow-up claim moves the fencing token like any other");

        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, task.PullRequestUrl, Now));
        task.State.Should().Be(TaskState.Done);
        task.FollowUpBranch.Should().BeNull("completion consumes the follow-up marker");
    }

    [Fact]
    public void Automatic_reopens_count_toward_the_closeout_budget_and_a_manual_reopen_resets_it()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(1);
        task.FollowUpKind.Should().Be(FollowUpKind.FailingChecks, "the launcher picks the fix-the-CI prompt from it");

        CompleteFollowUp(task);
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown, "completion consumes the follow-up marker");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Unresolved Copilot threads.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(2, "the budget spans the whole closeout, not a single reopen");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Human asked for another attempt.",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(0, "a human-initiated reopen restores the automatic budget");
    }

    private static void CompleteFollowUp(TaskAggregate task)
    {
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, task.PullRequestUrl, Now));
    }

    [Fact]
    public void Reopen_of_a_non_done_task_conflicts()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*only a done task*");
    }

    [Fact]
    public void Reopen_without_a_pull_request_conflicts()
    {
        TaskAggregate task = DoneTask(pullRequestUrl: null);

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*no pull request*");
    }

    [Fact]
    public void Reopen_without_a_branch_fails_validation()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, branch: " ", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*branch*");
    }

    [Fact]
    public void Retry_of_a_failed_task_requeues_and_the_next_claim_moves_the_fencing_token()
    {
        TaskAggregate task = FailedTask();
        Guid failedRunId = task.CurrentRunId!.Value;

        TaskRetried retried = TaskDecider.Retry(
            task, failedRunId, "task/abc-branch", "Daemon push bug fixed; the work is intact.", Now, DomainId.New());
        task.Apply(retried);

        task.State.Should().Be(TaskState.Queued);
        task.RetryBranch.Should().Be("task/abc-branch", "the launcher resumes the failed run's branch when it survives");
        task.ClaimedByNodeId.Should().BeNull("a retried task is queued, and queued work is unclaimed");
        task.CurrentRunId.Should().BeNull();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        claimed.LeaseGeneration.Should().Be(2, "a retry claim moves the fencing token like any other");

        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/9", Now));
        task.RetryBranch.Should().BeNull("completion consumes the retry marker");
    }

    [Fact]
    public void Retry_without_a_run_record_carries_no_branch_so_the_run_starts_clean()
    {
        TaskAggregate task = FailedTask();

        task.Apply(TaskDecider.Retry(task, previousRunId: null, branch: null, "Retrying anyway.", Now, DomainId.New()));

        task.State.Should().Be(TaskState.Queued);
        task.RetryBranch.Should().BeNull("no observed branch means a clean start from the base branch");
    }

    [Fact]
    public void Retry_of_a_non_failed_task_conflicts()
    {
        TaskAggregate queued = QueuedTask();
        Action retryQueued = () => TaskDecider.Retry(queued, null, null, "reason", Now, DomainId.New());
        retryQueued.Should().Throw<DomainConflictException>().WithMessage("*only a failed task*");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action retryAbandoned = () => TaskDecider.Retry(abandoned, null, null, "reason", Now, DomainId.New());
        retryAbandoned.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    [Fact]
    public void Retry_without_a_reason_fails_validation()
    {
        TaskAggregate task = FailedTask();

        Action act = () => TaskDecider.Retry(task, task.CurrentRunId, "task/abc", reason: " ", Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Failed_is_a_waypoint_and_only_done_and_abandoned_are_terminal()
    {
        FailedTask().State.IsTerminal.Should().BeFalse(
            "an unsolved problem is not an ending — Failed waits for a human (log #27)");
        TaskState.Done.IsTerminal.Should().BeTrue();
        TaskState.Abandoned.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Fail_of_an_already_failed_task_stays_rejected()
    {
        TaskAggregate task = FailedTask();

        TaskDecider.CanFail(task).Should().BeFalse("the daemon's pre-check mirrors the guard");
        Action act = () => TaskDecider.Fail(task, DomainId.New(), "another failure", Now);
        act.Should().Throw<DomainConflictException>().WithMessage("*already Failed*");
    }

    [Fact]
    public void Fail_of_a_done_or_abandoned_task_still_conflicts()
    {
        TaskAggregate done = DoneTask("https://github.com/x/y/pull/7");
        Action failDone = () => TaskDecider.Fail(done, DomainId.New(), "too late", Now);
        failDone.Should().Throw<DomainConflictException>().WithMessage("*already Done*");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action failAbandoned = () => TaskDecider.Fail(abandoned, DomainId.New(), "too late", Now);
        failAbandoned.Should().Throw<DomainConflictException>().WithMessage("*already Abandoned*");
    }

    [Fact]
    public void Resolve_of_a_failed_task_reaches_done_and_records_where_the_work_landed()
    {
        TaskAggregate task = FailedTask();

        TaskResolved resolved = TaskDecider.Resolve(
            task, "Work merged as PR #7; only the push step failed.",
            "https://github.com/x/y/pull/7", Now, DomainId.New());
        task.Apply(resolved);

        task.State.Should().Be(TaskState.Done);
        task.State.IsTerminal.Should().BeTrue("resolve is an explicit exit to an ending");
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7");
    }

    [Fact]
    public void Resolve_without_a_pull_request_keeps_the_one_already_on_the_stream()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Fail(task, task.CurrentRunId!.Value, "Follow-up push rejected.", Now));

        task.Apply(TaskDecider.Resolve(task, "The follow-up's work is on the PR already.", null, Now, DomainId.New()));

        task.State.Should().Be(TaskState.Done);
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7", "resolve never erases what was observed");
        task.FollowUpBranch.Should().BeNull("resolution consumes the pending follow-up marker");
    }

    [Fact]
    public void Resolved_task_with_a_pull_request_can_reopen_for_closeout_like_any_done_task()
    {
        TaskAggregate task = FailedTask();
        task.Apply(TaskDecider.Resolve(
            task, "Merged by hand as PR #7.", "https://github.com/x/y/pull/7", Now, DomainId.New()));

        TaskReopened reopened = TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Unresolved review comments",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        reopened.Should().NotBeNull("a resolved task is Done like any other, PR levers included");
    }

    [Fact]
    public void Resolve_of_a_non_failed_task_conflicts()
    {
        TaskAggregate queued = QueuedTask();
        Action resolveQueued = () => TaskDecider.Resolve(queued, "reason", null, Now, DomainId.New());
        resolveQueued.Should().Throw<DomainConflictException>().WithMessage("*only a failed task*");

        TaskAggregate done = DoneTask("https://github.com/x/y/pull/7");
        Action resolveDone = () => TaskDecider.Resolve(done, "reason", null, Now, DomainId.New());
        resolveDone.Should().Throw<DomainConflictException>("a done task has nothing to resolve");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action resolveAbandoned = () => TaskDecider.Resolve(abandoned, "reason", null, Now, DomainId.New());
        resolveAbandoned.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    [Fact]
    public void Resolve_without_a_reason_fails_validation()
    {
        TaskAggregate task = FailedTask();

        Action act = () => TaskDecider.Resolve(task, reason: " ", null, Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Abandon_of_a_failed_task_is_the_walk_away_exit()
    {
        TaskAggregate task = FailedTask();

        task.Apply(TaskDecider.Abandon(task, "not worth another run", Now, DomainId.New()));

        task.State.Should().Be(TaskState.Abandoned,
            "walking away from a failure is the human ending Abandoned exists for (log #27)");
    }

    [Fact]
    public void Abandon_consumes_the_pending_work_markers()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));

        task.Apply(TaskDecider.Abandon(task, "not worth fixing", Now, DomainId.New()));

        task.FollowUpBranch.Should().BeNull("an abandoned task has no follow-up run pending");
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
    }

    [Fact]
    public void Abandon_after_a_retry_consumes_the_retry_marker()
    {
        TaskAggregate task = FailedTask();
        task.Apply(TaskDecider.Retry(
            task, task.CurrentRunId, "task/abc-branch", "One more attempt.", Now, DomainId.New()));

        task.Apply(TaskDecider.Abandon(task, "second thoughts", Now, DomainId.New()));

        task.RetryBranch.Should().BeNull("an abandoned task has no retry pending");
    }

    [Fact]
    public void Abandon_clears_the_pending_question_so_a_late_answer_cannot_resurrect_the_task()
    {
        TaskAggregate task = ClaimedTask();
        Guid questionId = DomainId.New();
        task.Apply(TaskDecider.Ask(task, questionId, task.CurrentRunId!.Value, "Which config wins?", Now));

        task.Apply(TaskDecider.Abandon(task, "no longer needed", Now, DomainId.New()));

        task.PendingQuestionId.Should().BeNull("Answer guards on the pending question, not on state");
        Action act = () => TaskDecider.Answer(task, questionId, "too late", Now, DomainId.New());
        act.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    private static TaskAggregate FailedTask()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Fail(task, task.CurrentRunId!.Value, "Push rejected: branch was rebased.", Now));
        return task;
    }

    private static TaskAggregate DoneTask(string? pullRequestUrl)
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, pullRequestUrl, Now));
        return task;
    }

    /// <summary>
    /// The owner these helpers assign to. Assignment is the dispatch trigger and the claim
    /// guard reads it (Decisions Log #34), so a task only reaches Queued through a named owner.
    /// </summary>
    private static readonly Guid Owner = DomainId.New();

    private static TaskAggregate DraftTask(params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit", "tests cover the limiter"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy));
        return task;
    }

    private static TaskAggregate PublishedTask(TaskDependencyGraph? graph = null)
    {
        TaskAggregate task = DraftTask();
        task.Apply(TaskDecider.Publish(task, graph ?? TaskDependencyGraph.Empty, Now, Owner));
        return task;
    }

    private static TaskAggregate QueuedTask()
    {
        TaskAggregate task = PublishedTask();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }

    private static TaskAggregate ClaimedTask()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        return task;
    }
}
