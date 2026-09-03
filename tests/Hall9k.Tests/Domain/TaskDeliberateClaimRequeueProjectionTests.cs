using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A deliberately-claimed Blocked task (h9k task start --acknowledge-unmet-dependencies) still
/// carries its open blocker in UnmetDependencies while Claimed — TaskClaimed never clears it,
/// only TaskAssigned does. Giving that claim back (TaskRequeued, TaskRetried, TaskHandedBack) must
/// land these views on Blocked, not Queued, or the daemon's plain state = Queued queue query
/// dispatches the task headless behind a blocker nobody ever cleared (independent pre-PR review,
/// cycle 1, both lenses). TaskDeciderTests covers the identical walk on the aggregate itself.
/// </summary>
public sealed class TaskDeliberateClaimRequeueProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Task_details_returns_to_blocked_not_queued_on_requeue()
    {
        TaskDetailsProjection projection = new();
        (Guid id, Guid blockerId, Guid runId, TaskDetails view) = ClaimedBlockedTaskDetails(projection);

        projection.Apply(new FakeEvent<TaskRequeued>(new TaskRequeued(
            id, RequeueReason.HumanRequested, Now.AddHours(2))), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
        view.ClaimedByNodeId.Should().BeNull();
        view.CurrentRunId.Should().BeNull();
    }

    [Fact]
    public void Task_details_returns_to_blocked_not_queued_on_handback()
    {
        TaskDetailsProjection projection = new();
        (Guid id, Guid blockerId, Guid runId, TaskDetails view) = ClaimedBlockedTaskDetails(projection);

        projection.Apply(new FakeEvent<TaskHandedBack>(new TaskHandedBack(
            id, runId, "task/x", null, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
    }

    [Fact]
    public void Task_details_returns_to_blocked_not_queued_on_retry()
    {
        TaskDetailsProjection projection = new();
        (Guid id, Guid blockerId, Guid runId, TaskDetails view) = ClaimedBlockedTaskDetails(projection);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, runId, "worktree cut failed", Now.AddHours(2))), view);

        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, runId, "task/x", "trying again", Now.AddHours(3), DomainId.New())), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
    }

    [Fact]
    public void Task_list_item_returns_to_blocked_not_queued_on_requeue()
    {
        TaskListItemProjection projection = new();
        (Guid id, Guid blockerId, _, TaskListItem view) = ClaimedBlockedTaskListItem(projection);

        projection.Apply(new FakeEvent<TaskRequeued>(new TaskRequeued(
            id, RequeueReason.HumanRequested, Now.AddHours(2))), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
    }

    [Fact]
    public void Task_list_item_returns_to_blocked_not_queued_on_handback()
    {
        TaskListItemProjection projection = new();
        (Guid id, Guid blockerId, Guid runId, TaskListItem view) = ClaimedBlockedTaskListItem(projection);

        projection.Apply(new FakeEvent<TaskHandedBack>(new TaskHandedBack(
            id, runId, "task/x", null, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
    }

    [Fact]
    public void Task_list_item_returns_to_blocked_not_queued_on_retry()
    {
        TaskListItemProjection projection = new();
        (Guid id, Guid blockerId, Guid runId, TaskListItem view) = ClaimedBlockedTaskListItem(projection);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, runId, "worktree cut failed", Now.AddHours(2))), view);

        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, runId, "task/x", "trying again", Now.AddHours(3), DomainId.New())), view);

        view.State.Should().Be(TaskState.Blocked, "the open dependency is still on record unmet");
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
    }

    private static (Guid Id, Guid BlockerId, Guid RunId, TaskDetails View) ClaimedBlockedTaskDetails(
        TaskDetailsProjection projection)
    {
        Guid id = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(
            id, DomainId.New(), [blockerId], Now, DomainId.New())), view);
        view.State.Should().Be(TaskState.Blocked, "the assignment itself is what leaves it Blocked");

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, Guid.Empty, DomainId.New(), 1, runId, Now.AddHours(1), true)), view);
        view.State.Should().Be(TaskState.Claimed);
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId,
            "ClaimDeliberately never clears the dependency snapshot Assign recorded");

        return (id, blockerId, runId, view);
    }

    private static (Guid Id, Guid BlockerId, Guid RunId, TaskListItem View) ClaimedBlockedTaskListItem(
        TaskListItemProjection projection)
    {
        Guid id = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(
            id, DomainId.New(), [blockerId], Now, DomainId.New())), view);
        view.State.Should().Be(TaskState.Blocked, "the assignment itself is what leaves it Blocked");

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, Guid.Empty, DomainId.New(), 1, runId, Now.AddHours(1), true)), view);
        view.State.Should().Be(TaskState.Claimed);
        view.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId,
            "ClaimDeliberately never clears the dependency snapshot Assign recorded");

        return (id, blockerId, runId, view);
    }
}
