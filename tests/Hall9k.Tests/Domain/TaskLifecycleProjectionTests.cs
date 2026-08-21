using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Both read models through the lifecycle split (Decisions Log #34), including the migration
/// promise: a stream written before the split replays exactly as it behaved, so no historical
/// task is stranded in a state the platform did not have when it was written.
/// </summary>
public sealed class TaskLifecycleProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_task_added_before_the_lifecycle_existed_replays_queued_and_assigned_to_the_owner_who_added_it()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();

        // The historical event shape: no BlockedBy, no StartsAsDraft. The defaults are the
        // pre-split meaning, so replay never invents a state this stream never had.
        TaskAdded historical = new(
            id, DomainId.New(), "Written before the split", ["it is done"], TaskType.Feature,
            null, null, null, Now, ownerId);

        TaskAggregate aggregate = new();
        aggregate.Apply(historical);
        aggregate.State.Should().Be(TaskState.Queued);
        aggregate.AssignedOwnerId.Should().Be(ownerId, "the sole owner of a v0 install added it");

        TaskListItem list = new TaskListItemProjection().Create(new FakeEvent<TaskAdded>(historical));
        list.State.Should().Be(TaskState.Queued);
        list.AssignedOwnerId.Should().Be(ownerId);

        TaskDetails details = new TaskDetailsProjection().Create(new FakeEvent<TaskAdded>(historical));
        details.State.Should().Be(TaskState.Queued);
        details.AssignedOwnerId.Should().Be(ownerId);
    }

    [Fact]
    public void The_list_row_walks_draft_published_blocked_and_queued()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        TaskListItemProjection projection = new();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, dependencyId)));
        view.State.Should().Be(TaskState.Draft);
        view.BlockedBy.Should().Equal(dependencyId);

        projection.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), view);
        view.State.Should().Be(TaskState.Published);

        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(id, ownerId, [dependencyId], Now, ownerId)), view);
        view.State.Should().Be(TaskState.Blocked);
        view.AssignedOwnerId.Should().Be(ownerId, "the daemon's queue query filters on exactly this");

        projection.Apply(
            new FakeEvent<TaskDependencyCompleted>(new TaskDependencyCompleted(id, dependencyId, [], Now)), view);
        view.State.Should().Be(TaskState.Queued);
        view.UnmetDependencies.Should().BeEmpty();
    }

    [Fact]
    public void A_dead_blocker_leaves_the_row_blocked_with_the_reason_the_pane_reads()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        TaskListItemProjection projection = new();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, dependencyId)));
        projection.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), view);
        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(id, ownerId, [dependencyId], Now, ownerId)), view);

        projection.Apply(new FakeEvent<TaskDependencyFailed>(
            new TaskDependencyFailed(id, dependencyId, "Its blocker was abandoned.", Now)), view);

        view.State.Should().Be(TaskState.Blocked, "unblocking it would dispatch work whose premise died");
        view.DependencyFailureReason.Should().Be("Its blocker was abandoned.");
    }

    [Fact]
    public void A_recovered_blocker_returns_both_read_models_to_the_ordinary_waiting_on_display()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        TaskListItemProjection list = new();
        TaskDetailsProjection details = new();

        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, dependencyId)));
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, dependencyId)));

        TaskPublished published = new(id, Now, ownerId);
        list.Apply(new FakeEvent<TaskPublished>(published), row);
        details.Apply(new FakeEvent<TaskPublished>(published), detail);

        TaskAssigned assigned = new(id, ownerId, [dependencyId], Now, ownerId);
        list.Apply(new FakeEvent<TaskAssigned>(assigned), row);
        details.Apply(new FakeEvent<TaskAssigned>(assigned), detail);

        TaskDependencyFailed died = new(id, dependencyId, "Its blocker failed.", Now);
        list.Apply(new FakeEvent<TaskDependencyFailed>(died), row);
        details.Apply(new FakeEvent<TaskDependencyFailed>(died), detail);

        TaskDependencyRecovered lifted = new(id, dependencyId, "It is Queued again.", Now.AddHours(1));
        list.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), row);
        details.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), detail);

        row.State.Should().Be(TaskState.Blocked, "the blocker still has to finish before this may run");
        row.DeadDependencies.Should().BeEmpty();
        row.UnmetDependencies.Should().Equal([dependencyId], "it waits on the blocker the ordinary way now");
        row.DependencyFailureReason.Should().BeNull("this is what makes h9k status stop reading NeedsHuman");

        detail.State.Should().Be(TaskState.Blocked);
        detail.DeadDependencies.Should().BeEmpty();
        detail.DependencyFailureReason.Should().BeNull();
    }

    [Fact]
    public void A_recovery_that_lost_the_race_leaves_a_row_that_still_has_a_dead_blocker_alone()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid firstBlocker = DomainId.New();
        Guid secondBlocker = DomainId.New();
        TaskListItemProjection list = new();

        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, firstBlocker, secondBlocker)));
        list.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), row);
        list.Apply(
            new FakeEvent<TaskAssigned>(new TaskAssigned(id, ownerId, [firstBlocker, secondBlocker], Now, ownerId)),
            row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(
            new TaskDependencyFailed(id, firstBlocker, "The first one failed.", Now)), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(
            new TaskDependencyFailed(id, secondBlocker, "The second one was abandoned.", Now)), row);

        // Two nodes observed the same retry, so the second recovery finds nothing to remove.
        TaskDependencyRecovered lifted = new(id, firstBlocker, "It is Queued again.", Now.AddHours(1));
        list.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), row);
        list.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), row);

        row.DeadDependencies.Should().Equal([secondBlocker]);
        row.DependencyFailureReason.Should().Be(
            "The second one was abandoned.",
            "a duplicate recovery must not wipe the reason off a task that is still held");
    }

    [Fact]
    public void Both_read_models_replay_a_restated_then_completed_hold_the_way_the_aggregate_does()
    {
        // The same events through all three, asserted against each other rather than against a
        // transcription of the expected answer: a read model that quietly disagreed with the
        // aggregate would leave h9k task show advising a human about a blocker that is Done
        // (review finding, 2026-08-21).
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid restated = DomainId.New();
        Guid abandoned = DomainId.New();

        TaskAdded added = Drafted(id, ownerId, restated, abandoned);
        TaskPublished published = new(id, Now, ownerId);
        TaskAssigned assigned = new(id, ownerId, [restated, abandoned], Now, ownerId);
        TaskDependencyFailed died = new(id, restated, "It failed.", Now);
        TaskDependencyFailed otherDied = new(id, abandoned, "The other one was abandoned.", Now.AddMinutes(1));
        // The first blocker died a different death since, so its hold is restated — which makes
        // it the newest dead one, in the read models as much as in the aggregate.
        TaskDependencyFailed restatedDeath = new(
            id, restated, "It reads Done on a run that never merged.", Now.AddHours(1));
        // And then it was put back under watch and actually merged. What the human is left
        // reading is the death that still stands, not the one that just closed out.
        TaskDependencyCompleted closedOut = new(id, restated, [abandoned], Now.AddHours(2));

        TaskListItemProjection list = new();
        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(added));
        list.Apply(new FakeEvent<TaskPublished>(published), row);
        list.Apply(new FakeEvent<TaskAssigned>(assigned), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(died), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(otherDied), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(restatedDeath), row);
        list.Apply(new FakeEvent<TaskDependencyCompleted>(closedOut), row);

        TaskDetailsProjection details = new();
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(added));
        details.Apply(new FakeEvent<TaskPublished>(published), detail);
        details.Apply(new FakeEvent<TaskAssigned>(assigned), detail);
        details.Apply(new FakeEvent<TaskDependencyFailed>(died), detail);
        details.Apply(new FakeEvent<TaskDependencyFailed>(otherDied), detail);
        details.Apply(new FakeEvent<TaskDependencyFailed>(restatedDeath), detail);
        details.Apply(new FakeEvent<TaskDependencyCompleted>(closedOut), detail);

        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        aggregate.Apply(published);
        aggregate.Apply(assigned);
        aggregate.Apply(died);
        aggregate.Apply(otherDied);
        aggregate.Apply(restatedDeath);
        aggregate.Apply(closedOut);

        aggregate.DeadDependencies.Should().Equal(abandoned);
        aggregate.DependencyFailureReason.Should().Be("The other one was abandoned.");
        row.DeadDependencies.Should().Equal(aggregate.DeadDependencies, "one stream, one state");
        row.DependencyFailureReason.Should().Be(aggregate.DependencyFailureReason);
        detail.DeadDependencies.Should().Equal(aggregate.DeadDependencies);
        detail.DependencyFailureReason.Should().Be(aggregate.DependencyFailureReason);
    }

    [Fact]
    public void A_hold_recorded_while_a_recovery_was_in_flight_survives_it_in_both_read_models()
    {
        // The concurrency a recovery cannot see: one pass decides the retried blocker is back
        // while another appends a second blocker's death in between. What survives is derived
        // where the event is applied, so the newer hold stands (review finding, 2026-08-21).
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid retried = DomainId.New();
        Guid diedMeanwhile = DomainId.New();

        TaskAdded added = Drafted(id, ownerId, retried, diedMeanwhile);
        TaskPublished published = new(id, Now, ownerId);
        TaskAssigned assigned = new(id, ownerId, [retried, diedMeanwhile], Now, ownerId);
        TaskDependencyFailed died = new(id, retried, "It failed.", Now);
        TaskDependencyFailed otherDied = new(
            id, diedMeanwhile, "The other one was abandoned.", Now.AddMinutes(30));
        TaskDependencyRecovered lifted = new(id, retried, "It is Queued again.", Now.AddHours(1));

        TaskListItemProjection list = new();
        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(added));
        list.Apply(new FakeEvent<TaskPublished>(published), row);
        list.Apply(new FakeEvent<TaskAssigned>(assigned), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(died), row);
        list.Apply(new FakeEvent<TaskDependencyFailed>(otherDied), row);
        list.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), row);

        TaskDetailsProjection details = new();
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(added));
        details.Apply(new FakeEvent<TaskPublished>(published), detail);
        details.Apply(new FakeEvent<TaskAssigned>(assigned), detail);
        details.Apply(new FakeEvent<TaskDependencyFailed>(died), detail);
        details.Apply(new FakeEvent<TaskDependencyFailed>(otherDied), detail);
        details.Apply(new FakeEvent<TaskDependencyRecovered>(lifted), detail);

        row.DeadDependencies.Should().Equal(diedMeanwhile);
        row.DependencyFailureReason.Should().Be(
            "The other one was abandoned.",
            "a hold silenced by a stale snapshot would stay silenced: every later sweep finds "
            + "that death already recorded and says nothing new");
        detail.DeadDependencies.Should().Equal(diedMeanwhile);
        detail.DependencyFailureReason.Should().Be("The other one was abandoned.");
    }

    [Fact]
    public void Revising_a_draft_records_only_the_fields_it_was_given()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        TaskDetailsProjection projection = new();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId)));
        projection.Apply(new FakeEvent<TaskRevised>(new TaskRevised(
            id,
            Optional<string>.None,
            Optional<IReadOnlyList<string>>.Of(["a sharper criterion"]),
            Optional<string>.Of("Read PLAN.md §16 first"),
            Optional<IReadOnlyList<Guid>>.None,
            Optional<TaskType>.None,
            Optional<AgentModel>.None,
            Now,
            ownerId)), view);

        view.Objective.Should().Be("Develop me", "an untouched field is left alone");
        view.AcceptanceCriteria.Should().Equal("a sharper criterion");
        view.AgentContext.Should().Be("Read PLAN.md §16 first");
        view.Revisions.Should().Be(1);
    }

    [Fact]
    public void Unassigning_returns_the_detail_row_to_published_with_no_assignee_left_on_it()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        TaskDetailsProjection projection = new();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId)));
        projection.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), view);
        projection.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(id, ownerId, [], Now, ownerId)), view);
        projection.Apply(new FakeEvent<TaskUnassigned>(new TaskUnassigned(id, "Not yet", Now, ownerId)), view);

        view.State.Should().Be(TaskState.Published);
        view.AssignedOwnerId.Should().BeNull();
    }

    [Fact]
    public void A_dependency_event_that_lost_the_race_to_an_unassign_replays_as_a_no_op()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid firstBlocker = DomainId.New();
        Guid secondBlocker = DomainId.New();

        // The resolver reads a Blocked task, a human unassigns it, and only then does the
        // resolver's append land. Every dependency Apply is guarded on Blocked so the late
        // event cannot put unmet dependencies or a failure reason back on a Published task.
        TaskAggregate aggregate = new();
        aggregate.Apply(Drafted(id, ownerId, firstBlocker, secondBlocker));
        aggregate.Apply(new TaskPublished(id, Now, ownerId));
        aggregate.Apply(new TaskAssigned(id, ownerId, [firstBlocker, secondBlocker], Now, ownerId));
        aggregate.Apply(new TaskUnassigned(id, "Changed my mind", Now, ownerId));

        aggregate.Apply(new TaskDependencyCompleted(id, firstBlocker, [secondBlocker], Now));
        aggregate.Apply(new TaskDependencyFailed(id, secondBlocker, "Its blocker was abandoned.", Now));

        aggregate.State.Should().Be(TaskState.Published);
        aggregate.UnmetDependencies.Should().BeEmpty("unassigning cleared the bookkeeping and nothing put it back");
        aggregate.DeadDependencies.Should().BeEmpty();
        aggregate.DependencyFailureReason.Should().BeNull();
    }

    [Fact]
    public void The_read_models_ignore_dependency_events_that_land_after_the_task_left_blocked()
    {
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid firstBlocker = DomainId.New();
        Guid secondBlocker = DomainId.New();
        TaskListItemProjection list = new();
        TaskDetailsProjection details = new();

        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, firstBlocker, secondBlocker)));
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(Drafted(id, ownerId, firstBlocker, secondBlocker)));

        list.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), row);
        details.Apply(new FakeEvent<TaskPublished>(new TaskPublished(id, Now, ownerId)), detail);

        TaskAssigned assigned = new(id, ownerId, [firstBlocker, secondBlocker], Now, ownerId);
        list.Apply(new FakeEvent<TaskAssigned>(assigned), row);
        details.Apply(new FakeEvent<TaskAssigned>(assigned), detail);

        TaskUnassigned unassigned = new(id, "Changed my mind", Now, ownerId);
        list.Apply(new FakeEvent<TaskUnassigned>(unassigned), row);
        details.Apply(new FakeEvent<TaskUnassigned>(unassigned), detail);

        TaskDependencyCompleted completed = new(id, firstBlocker, [secondBlocker], Now);
        list.Apply(new FakeEvent<TaskDependencyCompleted>(completed), row);
        details.Apply(new FakeEvent<TaskDependencyCompleted>(completed), detail);

        TaskDependencyFailed died = new(id, secondBlocker, "Its blocker was abandoned.", Now);
        list.Apply(new FakeEvent<TaskDependencyFailed>(died), row);
        details.Apply(new FakeEvent<TaskDependencyFailed>(died), detail);

        row.State.Should().Be(TaskState.Published);
        row.UnmetDependencies.Should().BeEmpty("a late append must not resurrect a stale unmet set");
        row.DependencyFailureReason.Should().BeNull("h9k task show would print a blocker the task no longer has");

        detail.State.Should().Be(TaskState.Published);
        detail.UnmetDependencies.Should().BeEmpty();
        detail.DependencyFailureReason.Should().BeNull();
    }

    private static TaskAdded Drafted(Guid id, Guid ownerId, params Guid[] blockedBy) => new(
        id, DomainId.New(), "Develop me", ["it is done"], TaskType.Feature,
        null, null, null, Now, ownerId, null, blockedBy, StartsAsDraft: true);
}
