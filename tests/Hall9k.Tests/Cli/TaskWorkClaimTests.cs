using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The atomic decision behind h9k task work's Published entry (task 688a1ccf-h9k):
/// <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/> composes
/// <see cref="TaskDecider.Assign"/> and <see cref="TaskDecider.ClaimInteractively"/> into one
/// unit, with no session and no append, so the composition itself is pinned here without a
/// database — the concurrency arbitration this composition feeds is pinned separately, against a
/// real store, in TaskWorkClaimConcurrencyTests.
/// </summary>
public sealed class TaskWorkClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void A_published_task_with_no_open_dependencies_is_assigned_and_claimed_in_one_unit()
    {
        TaskAggregate task = PublishedTask();
        Guid runId = DomainId.New();

        (TaskAssigned assigned, TaskClaimed claimed) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            task, Owner, [], runId, Now);

        assigned.AssignedOwnerId.Should().Be(Owner);
        assigned.UnmetDependencies.Should().BeEmpty();

        claimed.NodeId.Should().Be(Guid.Empty, "an interactive claim carries the sentinel node id");
        claimed.OwnerId.Should().Be(Owner);
        claimed.RunId.Should().Be(runId);
        claimed.LeaseGeneration.Should().Be(1);

        // Mutated in place (mirrors TaskPublishCommand's own append-then-Apply composition), so
        // the caller's own aggregate reflects exactly what the atomic append is about to commit —
        // Queued, never Claimed, since this helper hands the claim event back rather than
        // applying it itself.
        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(Owner);
    }

    [Fact]
    public void A_published_task_with_a_closed_out_dependency_is_still_assigned_and_claimed()
    {
        TaskDependency closed = ClosedDependency();
        TaskAggregate task = PublishedTask(closed.Id);

        (TaskAssigned assigned, TaskClaimed claimed) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            task, Owner, [closed], DomainId.New(), Now);

        assigned.UnmetDependencies.Should().BeEmpty("the one dependency has already closed out");
        claimed.OwnerId.Should().Be(Owner);
        task.State.Should().Be(TaskState.Queued);
    }

    [Fact]
    public void A_published_task_with_an_open_dependency_is_refused_before_anything_is_decided()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = PublishedTask(open.Id);

        Action act = () => TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            task, Owner, [open], DomainId.New(), Now);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s) that have not closed out*")
            .Where(exception => exception.Message.Contains(open.Describe()),
                "the refusal names the open blocker exactly as h9k task assign's own Blocked landing would");

        // The refusal is up front: nothing about the task was decided, so it stays exactly what
        // it was handed in as, and a caller that retries once the blocker clears reads a task
        // still Published rather than one half-assigned toward a Blocked landing it never wanted.
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
    }

    private static TaskAggregate PublishedTask(params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Prove the atomic claim composes assign and claim",
            ["one event append, exactly one winner"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy);
        task.Apply(added);

        // Publish's own graph only needs to know each blocker exists, to clear its cycle check —
        // the dependency snapshot that actually drives Assign's decision in each test below is
        // the caller-supplied list passed straight to PrepareInteractiveClaimFromPublished.
        TaskDependencyGraph graph = blockedBy.Length == 0
            ? TaskDependencyGraph.Empty
            : new TaskDependencyGraph(blockedBy.Select(id => new TaskDependency(
                id, "A blocker", TaskState.Done, IsClosedOut: true, CurrentRunState: null,
                PullRequestUrl: null, TaskType.Chore, [])));
        task.Apply(TaskDecider.Publish(task, graph, Now, Owner));
        return task;
    }

    private static TaskDependency ClosedDependency() => new(
        DomainId.New(), "A blocker already merged", TaskState.Done, IsClosedOut: true,
        CurrentRunState: RunState.Completed, PullRequestUrl: "https://github.com/x/y/pull/1",
        TaskType.Chore, []);

    private static TaskDependency OpenDependency() => new(
        DomainId.New(), "A blocker still running", TaskState.Claimed, IsClosedOut: false,
        CurrentRunState: RunState.Running, PullRequestUrl: null,
        TaskType.Chore, []);
}
