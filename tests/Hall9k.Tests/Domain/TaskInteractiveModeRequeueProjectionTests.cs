using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// TaskDetails mirrors TaskAggregate's own InteractiveModeEnabled bookkeeping (design ruling R6,
/// amended 2026-09-05): a default h9k task release (TaskRequeued.ClearInteractiveMode true) is a
/// second exit door alongside handback, while a release given --keep-interactive, or a node's own
/// lease expiring, leaves the flag alone. TaskDeciderTests covers the identical walk on the
/// aggregate itself.
/// </summary>
public sealed class TaskInteractiveModeRequeueProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Task_details_clears_interactive_mode_on_a_default_requeue()
    {
        TaskDetails view = InteractivelyClaimedTaskDetails(out Guid id);

        TaskDetailsProjection projection = new();
        projection.Apply(new FakeEvent<TaskRequeued>(new TaskRequeued(
            id, RequeueReason.HumanRequested, Now.AddHours(2), ClearInteractiveMode: true)), view);

        view.InteractiveModeEnabled.Should().BeFalse();
    }

    [Fact]
    public void Task_details_keeps_interactive_mode_on_a_requeue_that_does_not_clear_it()
    {
        TaskDetails view = InteractivelyClaimedTaskDetails(out Guid id);

        TaskDetailsProjection projection = new();
        projection.Apply(new FakeEvent<TaskRequeued>(new TaskRequeued(
            id, RequeueReason.LeaseExpired, Now.AddHours(2))), view);

        view.InteractiveModeEnabled.Should().BeTrue();
    }

    private static TaskDetails InteractivelyClaimedTaskDetails(out Guid id)
    {
        TaskDetailsProjection projection = new();
        id = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(
            id, DomainId.New(), [], Now, DomainId.New())), view);

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, Guid.Empty, DomainId.New(), 1, runId, Now.AddHours(1), InteractiveMode: true)), view);
        view.InteractiveModeEnabled.Should().BeTrue("ClaimInteractively always sets it");

        return view;
    }
}
