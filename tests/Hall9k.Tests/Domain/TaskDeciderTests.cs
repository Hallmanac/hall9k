using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
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

    [Fact]
    public void Add_without_acceptance_criteria_fails_the_readiness_contract()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Add rate limiting to auth endpoints",
            acceptanceCriteria: [" ", ""], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*acceptance criter*");
    }

    [Fact]
    public void Claim_increments_generation_and_carries_the_minted_run_id()
    {
        TaskAggregate task = QueuedTask();
        Guid runId = DomainId.New();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), DomainId.New(), runId, Now);

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

        Action act = () => TaskDecider.Claim(task, DomainId.New(), DomainId.New(), DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Requeue_after_claim_returns_to_queued_and_a_reclaim_bumps_generation_again()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now));
        task.State.Should().Be(TaskState.Queued);

        TaskClaimed second = TaskDecider.Claim(task, DomainId.New(), DomainId.New(), DomainId.New(), Now);
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

    private static TaskAggregate QueuedTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit", "tests cover the limiter"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New()));
        return task;
    }

    private static TaskAggregate ClaimedTask()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), DomainId.New(), DomainId.New(), Now));
        return task;
    }
}
