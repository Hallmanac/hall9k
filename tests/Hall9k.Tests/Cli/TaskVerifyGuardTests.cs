using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The claim and run-state guard <see cref="TaskVerifyCommand"/> moved into a static decision
/// (idea 6e6d0200, Card 6), pinned without a database the way <c>TaskStartClaimTests</c> pins its own.
/// The refusals matter beyond this command: the passage classification reads a gate recorded before
/// the run's <c>AgentSessionCompleted</c> as hand-run only because this guard admits nothing else.
/// </summary>
public sealed class TaskVerifyGuardTests
{
    private static readonly Guid RunId = DomainId.New();

    private static TaskDetails Claimed(Guid? claimedByNode = null, TaskState? state = null, TaskType? type = null) => new()
    {
        Id = DomainId.New(),
        State = state ?? TaskState.Claimed,
        Type = type ?? TaskType.Feature,
        ClaimedByNodeId = claimedByNode ?? Guid.Empty,
        CurrentRunId = RunId,
    };

    private static RunDetails Run(RunState state) => new() { Id = RunId, State = state };

    public static TheoryData<RunState> InThePersonsHands => new() { RunState.Dispatched, RunState.Running };

    public static TheoryData<RunState> HandedToTheDaemon => new() { RunState.Verifying, RunState.UnderReview, RunState.Completed };

    public static TheoryData<TaskState> NotClaimed => new() { TaskState.Queued, TaskState.Done, TaskState.Draft };

    [Theory]
    [MemberData(nameof(InThePersonsHands))]
    public void A_live_sentinel_claim_on_a_run_in_the_persons_hands_is_admitted(RunState state)
    {
        TaskDetails task = Claimed();
        RunDetails run = Run(state);

        TaskVerifyCommand.RequireVerifiableRun(task.Id, task, run).Should().BeSameAs(run);
    }

    [Fact]
    public void A_claim_held_by_a_real_node_is_refused_because_it_is_not_a_sentinel_claim()
    {
        TaskDetails task = Claimed(claimedByNode: DomainId.New());

        Action act = () => TaskVerifyCommand.RequireVerifiableRun(task.Id, task, Run(RunState.Running));

        act.Should().Throw<DomainConflictException>().WithMessage("*only a task with an active interactive claim*");
    }

    [Theory]
    [MemberData(nameof(NotClaimed))]
    public void A_task_that_is_not_claimed_is_refused(TaskState state)
    {
        TaskDetails task = Claimed(state: state);

        Action act = () => TaskVerifyCommand.RequireVerifiableRun(task.Id, task, Run(RunState.Running));

        act.Should().Throw<DomainConflictException>().WithMessage($"*is {state.Value}*");
    }

    [Fact]
    public void A_claim_with_no_run_on_record_is_refused_and_pointed_at_release()
    {
        TaskDetails task = Claimed();

        Action act = () => TaskVerifyCommand.RequireVerifiableRun(task.Id, task, currentRun: null);

        act.Should().Throw<DomainConflictException>().WithMessage("*has no record*h9k task release*");
    }

    [Theory]
    [MemberData(nameof(HandedToTheDaemon))]
    public void A_delivered_run_in_the_standard_pipeline_is_refused(RunState state)
    {
        TaskDetails task = Claimed();

        Action act = () => TaskVerifyCommand.RequireVerifiableRun(task.Id, task, Run(state));

        act.Should().Throw<DomainConflictException>().WithMessage($"*already {state.Value}*h9k task deliver*");
    }

    [Fact]
    public void A_pr_review_task_under_the_sentinel_is_refused_before_its_run_state_is_judged()
    {
        TaskDetails task = Claimed(type: TaskType.PrReview);

        Action act = () => TaskVerifyCommand.RequireVerifiableRun(task.Id, task, Run(RunState.Running));

        act.Should().Throw<DomainConflictException>();
    }
}
