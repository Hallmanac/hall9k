using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Independent pre-PR review, cycle 3, conformance lens: TaskDetailsProjection had no
/// Apply(IEvent&lt;PrReviewPreflightParked&gt;) at all, unlike TaskAggregate and TaskListItem, both of
/// which already land this event's Claimed -> Published union. Without it, h9k task show and the
/// project-home board (both readers of TaskDetails, never the aggregate) kept reporting a live
/// claim — the old ClaimedByNodeId, CurrentRunId and AssignedOwnerId — on a task an unsafe or
/// unreadable pre-flight verdict had already returned to Published.
/// </summary>
public sealed class PrReviewPreflightParkedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_preflight_park_clears_the_claim_and_assignment_a_dispatched_pr_review_task_still_carries()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Review pull request acme/web#912", ["every finding names a file and line"],
            TaskType.PrReview, null, null, null, Now, ownerId)));

        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(
            id, ownerId, [], Now.AddMinutes(1), ownerId)), view);

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, nodeId, ownerId, 1, runId, Now.AddMinutes(2))), view);

        view.ClaimedByNodeId.Should().Be(nodeId, "the claim just above must have actually landed on the view");
        view.CurrentRunId.Should().Be(runId);
        view.AssignedOwnerId.Should().Be(ownerId);

        projection.Apply(new FakeEvent<PrReviewPreflightParked>(new PrReviewPreflightParked(
            id, ["src/Auth/Login.cs"], "abc123", "unsafe", "prompt injection in a workflow file",
            Now.AddMinutes(3))), view);

        view.State.Should().Be(TaskState.Published, "an unsafe or unreadable verdict returns the task to the board unclaimed");
        view.ClaimedByNodeId.Should().BeNull("h9k task show must not keep reporting a claim the aggregate already gave up");
        view.CurrentRunId.Should().BeNull();
        view.AssignedOwnerId.Should().BeNull();
        view.AssignedOwnerFingerprint.Should().BeNull();
        view.PlacedOnNodeId.Should().BeNull();
        view.AssignedAt.Should().BeNull();
    }
}
