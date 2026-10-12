using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// <see cref="EventReplicationInbox.IsLateHolderRelease"/> is the verdict behind dropping a holder
/// release that reaches a task another node has claimed since: <see cref="TaskHolderReleased"/>
/// names no node, so applying it would clear the new holder (origin incident, 2026-10-12, where
/// two tasks finished with the ledger record still naming the node that had closed them out).
/// </summary>
public sealed class EventReplicationInboxLateHolderReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_release_from_a_node_other_than_the_current_holder_is_late()
    {
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        TaskAggregate task = ClaimedBy(nodeB);

        EventReplicationInbox.IsLateHolderRelease(task, originNodeId: nodeA).Should().BeTrue(
            "node B claimed the task before node A's release replicated");
    }

    [Fact]
    public void A_release_from_the_current_holder_applies()
    {
        Guid nodeB = DomainId.New();

        EventReplicationInbox.IsLateHolderRelease(ClaimedBy(nodeB), originNodeId: nodeB).Should().BeFalse();
    }

    [Fact]
    public void A_release_on_a_task_nobody_holds_applies()
    {
        TaskAggregate task = ClaimedBy(DomainId.New());
        task.Apply(new TaskHolderReleased(task.Id, Now));

        EventReplicationInbox.IsLateHolderRelease(task, originNodeId: DomainId.New()).Should().BeFalse(
            "there is no holder for the release to wipe");
    }

    [Fact]
    public void A_release_for_a_task_this_node_has_no_stream_for_applies()
    {
        EventReplicationInbox.IsLateHolderRelease(task: null, originNodeId: DomainId.New()).Should().BeFalse();
    }

    private static TaskAggregate ClaimedBy(Guid nodeId)
    {
        Guid ownerId = DomainId.New();
        Guid taskId = DomainId.New();
        (TaskAggregate task, _) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Claimed", ["done"], TaskType.Chore, null, null, null, Now, ownerId),
            ownerId, Now);
        task.Apply(TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now));
        return task;
    }
}
