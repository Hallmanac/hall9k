using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.CSharp.RuntimeBinder;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The assign/queue split on the task stream: assign records who holds a task and never queues it,
/// queue is the go signal (<see cref="TaskAssigned"/> on the wire), and dequeue, an interactive
/// release and a pre-flight park stop the go but keep the hold, while every event written before the
/// marker existed still clears it.
/// </summary>
public sealed class TaskQueueSplitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string RootA = "root-a";
    private const string RootB = "root-b";

    private static readonly Guid TaskId = Guid.Parse("01a10770-0f0d-774a-8b30-0954635b5e04");
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OwnerA = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid OwnerB = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");

    private static TaskAdded Draft() => new(
        TaskId, ProjectId, "Hold it", ["it is held"], TaskType.Feature, null, null, null, Now, OwnerA,
        StartsAsDraft: true);

    private static TaskPublished Published() => new(TaskId, Now, OwnerA);

    private static TaskAggregate PublishedTask()
    {
        TaskAggregate task = new();
        task.Apply(Draft());
        task.Apply(Published());
        return task;
    }

    private static TaskAggregate QueuedTask(Guid owner = default, string root = RootA)
    {
        TaskAggregate task = PublishedTask();
        task.Apply(TaskDecider.Assign(task, owner == default ? OwnerA : owner, [], Now, OwnerA, root));
        return task;
    }

    [Fact]
    public void Assigning_a_published_task_records_the_assignee_and_it_stays_published_and_unclaimable()
    {
        TaskAggregate task = PublishedTask();

        TaskAssigneeSet? set = TaskDecider.SetAssignee(task, OwnerB, RootB, assigneeIsActor: false, Now, OwnerA);
        task.Apply(set!);

        task.State.Should().Be(TaskState.Published, "assign records ownership only and never queues");
        task.AssigneeOwnerId.Should().Be(OwnerB);
        task.AssignedOwnerId.Should().BeNull("queued-for belongs to the go signal, and nothing queued it");
        Action claim = () => TaskDecider.Claim(task, Guid.NewGuid(), OwnerB, DomainId.New(), Now, RootB);
        claim.Should().Throw<DomainConflictException>("only a queued task is claimable");
    }

    [Fact]
    public void Queueing_a_task_nobody_holds_makes_the_actor_the_assignee_through_the_same_event()
    {
        TaskAggregate task = PublishedTask();
        task.AssigneeOwnerId.Should().BeNull();

        TaskAssigned queued = TaskDecider.Assign(task, OwnerA, [], Now, OwnerA, RootA);
        task.Apply(queued);

        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(OwnerA);
        task.AssigneeOwnerId.Should().Be(OwnerA, "TaskAssigned also lays hold of the task");
        task.AssigneeOwnerFingerprint.Should().Be(RootA);
    }

    [Fact]
    public void Queueing_a_task_its_assignee_holds_queues_it_for_that_assignee()
    {
        TaskAggregate task = PublishedTask();
        task.Apply(new TaskAssigneeSet(TaskId, OwnerA, RootA, Now, OwnerA));

        task.Apply(TaskDecider.Assign(task, task.AssigneeOwnerId!.Value, [], Now, OwnerA, task.AssigneeOwnerFingerprint));

        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(OwnerA);
        task.AssigneeOwnerId.Should().Be(OwnerA);
    }

    [Fact]
    public void Queueing_a_task_that_is_not_published_names_the_command_that_fixes_it()
    {
        TaskAggregate draft = new();
        draft.Apply(Draft());

        TaskAggregate queued = QueuedTask();

        Action queueDraft = () => TaskDecider.Assign(draft, OwnerA, [], Now, OwnerA, RootA);
        Action queueAgain = () => TaskDecider.Assign(queued, OwnerA, [], Now, OwnerA, RootA);

        queueDraft.Should().Throw<DomainConflictException>().WithMessage($"*h9k task publish {TaskId}*");
        queueAgain.Should().Throw<DomainConflictException>().WithMessage($"*h9k task dequeue {TaskId}*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dequeue_returns_a_queued_or_blocked_task_to_published_and_keeps_the_assignee(bool blocked)
    {
        TaskAggregate task = PublishedTask();
        Guid dependency = DomainId.New();
        task.Apply(blocked
            ? new TaskAssigned(TaskId, OwnerA, [dependency], Now, OwnerA, RootA)
            : TaskDecider.Assign(task, OwnerA, [], Now, OwnerA, RootA));
        TaskListItemProjection listProjection = new();
        TaskDetailsProjection detailsProjection = new();

        TaskUnassigned dequeued = TaskDecider.Dequeue(task, "waiting on a schema change", leaseHeld: false, Now, OwnerA);
        task.Apply(dequeued);
        TaskListItem list = ProjectQueued(listProjection, blocked, dequeued);
        TaskDetails details = ProjectQueued(detailsProjection, blocked, dequeued);

        dequeued.KeepsAssignee.Should().BeTrue();
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull("the go stopped");
        task.AssigneeOwnerId.Should().Be(OwnerA, "whoever held the task still does");
        task.AssigneeOwnerFingerprint.Should().Be(RootA);
        list.AssignedOwnerId.Should().BeNull();
        list.AssigneeOwnerId.Should().Be(OwnerA);
        details.AssigneeOwnerFingerprint.Should().Be(RootA);
    }

    [Fact]
    public void Unassign_on_a_queued_task_dequeues_and_clears_the_assignee_in_the_one_event()
    {
        TaskAggregate task = QueuedTask();

        TaskUnassigned unassigned = TaskDecider.Unassign(task, null, leaseHeld: false, Now, OwnerA);
        task.Apply(unassigned);

        unassigned.KeepsAssignee.Should().BeFalse();
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
        task.AssigneeOwnerId.Should().BeNull();
    }

    [Fact]
    public void A_dequeued_task_can_be_queued_again_for_the_same_assignee()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.Dequeue(task, null, leaseHeld: false, Now, OwnerA));

        task.Apply(TaskDecider.Assign(task, task.AssigneeOwnerId!.Value, [], Now, OwnerA, task.AssigneeOwnerFingerprint));

        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(OwnerA);
    }

    [Fact]
    public void Dequeue_is_refused_for_a_task_that_is_not_queued_and_while_a_lease_is_held()
    {
        TaskAggregate published = PublishedTask();
        TaskAggregate queued = QueuedTask();

        Action notQueued = () => TaskDecider.Dequeue(published, null, leaseHeld: false, Now, OwnerA);
        Action leased = () => TaskDecider.Dequeue(queued, null, leaseHeld: true, Now, OwnerA);

        notQueued.Should().Throw<DomainConflictException>().WithMessage("*not queued*");
        leased.Should().Throw<DomainConflictException>().WithMessage("*leased by a node*");
    }

    [Fact]
    public void A_TaskUnassigned_without_the_kept_marker_replays_as_a_clearing_unassign_and_one_with_it_keeps_the_hold()
    {
        TaskUnassigned legacy = new(TaskId, "left", Now, OwnerA);
        TaskUnassigned dequeue = legacy with { KeepsAssignee = true };

        legacy.KeepsAssignee.Should().BeFalse("an event written before the marker existed carries none");
        TaskAggregate cleared = QueuedTask();
        cleared.Apply(legacy);
        TaskAggregate kept = QueuedTask();
        kept.Apply(dequeue);

        cleared.AssigneeOwnerId.Should().BeNull();
        cleared.AssigneeOwnerFingerprint.Should().BeNull();
        kept.AssigneeOwnerId.Should().Be(OwnerA);
        kept.AssigneeOwnerFingerprint.Should().Be(RootA);
    }

    [Fact]
    public void Release_unassign_keeps_the_assignee_and_an_older_release_event_still_clears_it()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.ClaimInteractively(task, OwnerA, DomainId.New(), Now, ownerRootFingerprint: RootA));

        TaskInteractiveClaimUnassigned released = TaskDecider.ReleaseInteractiveClaimUnassigned(task, Now);
        TaskAggregate older = QueuedTask();
        older.Apply(TaskDecider.ClaimInteractively(older, OwnerA, DomainId.New(), Now, ownerRootFingerprint: RootA));
        older.Apply(new TaskInteractiveClaimUnassigned(TaskId, Now));
        task.Apply(released);

        released.KeepsAssignee.Should().BeTrue();
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
        task.AssigneeOwnerId.Should().Be(OwnerA);
        older.State.Should().Be(TaskState.Published);
        older.AssigneeOwnerId.Should().BeNull("a release written before the marker replays as it always did");
    }

    [Fact]
    public void A_pre_flight_park_keeps_the_assignee_and_an_older_park_still_clears_it()
    {
        TaskAggregate task = ClaimedTask();
        TaskAggregate older = ClaimedTask();

        PrReviewPreflightParked parked = TaskDecider.ParkPrReviewPreflight(task, ["src/x.cs"], "abc", "unsafe", "host", Now);
        task.Apply(parked);
        older.Apply(new PrReviewPreflightParked(TaskId, ["src/x.cs"], "abc", "unsafe", "host", Now));

        parked.KeepsAssignee.Should().BeTrue();
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
        task.AssigneeOwnerId.Should().Be(OwnerA);
        older.AssigneeOwnerId.Should().BeNull();
    }

    [Fact]
    public void The_projections_keep_the_assignee_through_a_release_and_a_park_and_clear_it_without_the_marker()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.ClaimInteractively(task, OwnerA, DomainId.New(), Now, ownerRootFingerprint: RootA));
        TaskInteractiveClaimUnassigned kept = TaskDecider.ReleaseInteractiveClaimUnassigned(task, Now);
        TaskInteractiveClaimUnassigned older = new(TaskId, Now);
        PrReviewPreflightParked parkedKept = new(TaskId, [], "abc", "unsafe", "host", Now, KeepsAssignee: true);
        PrReviewPreflightParked parkedOlder = new(TaskId, [], "abc", "unsafe", "host", Now);

        foreach ((object @event, bool keeps) in new (object, bool)[] { (kept, true), (older, false), (parkedKept, true), (parkedOlder, false) })
        {
            TaskListItem list = ProjectQueued(new TaskListItemProjection(), blocked: false, @event);
            TaskDetails details = ProjectQueued(new TaskDetailsProjection(), blocked: false, @event);

            (list.AssigneeOwnerId is not null).Should().Be(keeps, $"{@event.GetType().Name} list");
            (details.AssigneeOwnerId is not null).Should().Be(keeps, $"{@event.GetType().Name} details");
            list.AssignedOwnerId.Should().BeNull();
            details.AssignedOwnerId.Should().BeNull();
            list.State.Should().Be(TaskState.Published);
        }
    }

    private static TaskAggregate ClaimedTask()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.Claim(task, Guid.NewGuid(), OwnerA, DomainId.New(), Now, RootA));
        return task;
    }

    private static TaskListItem ProjectQueued(TaskListItemProjection projection, bool blocked, object @event)
    {
        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(Draft()));
        ApplyToProjection(projection, view, Published());
        ApplyToProjection(projection, view, new TaskAssigned(TaskId, OwnerA, blocked ? [DomainId.New()] : [], Now, OwnerA, RootA));
        return ApplyToProjection(projection, view, @event);
    }

    private static TaskDetails ProjectQueued(TaskDetailsProjection projection, bool blocked, object @event)
    {
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(Draft()));
        ApplyToProjection(projection, view, Published());
        ApplyToProjection(projection, view, new TaskAssigned(TaskId, OwnerA, blocked ? [DomainId.New()] : [], Now, OwnerA, RootA));
        return ApplyToProjection(projection, view, @event);
    }

    private static T ApplyToProjection<T>(object projection, T view, object @event)
        where T : class
    {
        Type fake = typeof(FakeEvent<>).MakeGenericType(@event.GetType());
        dynamic wrapped = Activator.CreateInstance(fake, @event)!;
        try
        {
            ((dynamic)projection).Apply(wrapped, (dynamic)view);
        }
        catch (RuntimeBinderException)
        {
            // An event neither read model reads changes nothing on either.
        }

        return view;
    }
}
