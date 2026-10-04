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
/// The assignee (who holds a task at any stage) kept apart from <see cref="TaskAggregate.AssignedOwnerId"/>
/// (the owner it is queued for): every pre-change stream replays with the two equal at every version, and
/// the new events move only the assignee, so nothing the dispatcher reads changes.
/// </summary>
public sealed class TaskAssigneeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string RootA = "root-a";
    private const string RootB = "root-b";

    private static readonly Guid TaskId = Guid.Parse("01a10743-4d93-75e9-a2ec-232d62d382cc");
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OwnerA = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid OwnerB = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid NodeA = Guid.Parse("cccccccc-cccc-7ccc-8ccc-cccccccccccc");
    private static readonly Guid NodeB = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");

    private static TaskAdded Draft() => new(
        TaskId, ProjectId, "Hold it", ["it is held"], TaskType.Feature, null, null, null, Now, OwnerA,
        StartsAsDraft: true);

    private static TaskAdded Legacy() => new(
        TaskId, ProjectId, "Written before the lifecycle", ["it is done"], TaskType.Feature, null, null, null, Now, OwnerA);

    private static TaskPublished Published() => new(TaskId, Now, OwnerA);

    private static TaskAssigned Assigned(Guid owner, string? root) =>
        new(TaskId, owner, [], Now, owner, root);

    private static TaskClaimed Claimed(Guid owner, string? root) =>
        new(TaskId, NodeA, owner, LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: root);

    /// <summary>
    /// One stream per door that sets or clears <see cref="TaskAggregate.AssignedOwnerId"/> today, none of
    /// which carries a new event, so each is a stream written before this change.
    /// </summary>
    public static TheoryData<string, object[]> PreChangeStreams() => new()
    {
        { "a legacy add, queued for the owner who added it", [Legacy()] },
        { "a legacy add, then an Owner-role override unassign", [Legacy(), new TaskUnassigned(TaskId, "left", Now, OwnerB, RootB, "teammate left")] },
        { "an assignment carrying a root fingerprint", [Draft(), Published(), Assigned(OwnerA, RootA)] },
        { "an assignment from before the fingerprint existed", [Draft(), Published(), Assigned(OwnerA, null)] },
        { "an assignment, then an ordinary unassign", [Draft(), Published(), Assigned(OwnerA, RootA), new TaskUnassigned(TaskId, null, Now, OwnerA)] },
        { "an assignment, then an Owner-role override unassign", [Draft(), Published(), Assigned(OwnerA, RootA), new TaskUnassigned(TaskId, "left", Now, OwnerB, RootA, "teammate left")] },
        { "an assignment, then a reassignment to somebody else", [Draft(), Published(), Assigned(OwnerA, RootA), new TaskUnassigned(TaskId, null, Now, OwnerA), Assigned(OwnerB, RootB)] },
        {
            "a forced takeover, then a release",
            [
                Draft(), Published(), Assigned(OwnerA, RootA), Claimed(OwnerA, RootA),
                new TaskHolderTakenOver(TaskId, NodeA, NodeB, OwnerB, RootB, "absent holder", OwnerB, Now),
                new TaskHolderReleased(TaskId, Now),
            ]
        },
        {
            "a cooperative grant",
            [
                Draft(), Published(), Assigned(OwnerA, RootA), Claimed(OwnerA, RootA),
                new TaskHolderReleased(TaskId, Now, NodeB, OwnerB, RootB),
            ]
        },
        {
            "an interactive release that unassigns",
            [Draft(), Published(), Assigned(OwnerA, RootA), Claimed(OwnerA, RootA), new TaskInteractiveClaimUnassigned(TaskId, Now)]
        },
        {
            "a pre-flight park",
            [
                Draft(), Published(), Assigned(OwnerA, RootA), Claimed(OwnerA, RootA),
                new PrReviewPreflightParked(TaskId, ["src/x.cs"], "abc", "unsafe", "touches the host", Now),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(PreChangeStreams))]
    public void A_stream_written_before_the_assignee_existed_projects_the_assignee_equal_to_queued_for_at_every_version(
        string door, object[] stream)
    {
        TaskAggregate aggregate = new();
        TaskListItem? list = null;
        TaskDetails? details = null;
        TaskListItemProjection listProjection = new();
        TaskDetailsProjection detailsProjection = new();

        foreach (object @event in stream)
        {
            aggregate.Apply((dynamic)@event);
            if (@event is TaskAdded added)
            {
                list = listProjection.Create(new FakeEvent<TaskAdded>(added));
                details = detailsProjection.Create(new FakeEvent<TaskAdded>(added));
            }
            else
            {
                list = ApplyToProjection(listProjection, list!, @event);
                details = ApplyToProjection(detailsProjection, details!, @event);
            }

            string at = $"{door}, after {@event.GetType().Name}";
            aggregate.AssigneeOwnerId.Should().Be(aggregate.AssignedOwnerId, at);
            aggregate.AssigneeOwnerFingerprint.Should().Be(aggregate.AssignedOwnerFingerprint, at);
            list!.AssigneeOwnerId.Should().Be(list.AssignedOwnerId, at);
            list.AssigneeOwnerFingerprint.Should().Be(list.AssignedOwnerFingerprint, at);
            details!.AssigneeOwnerId.Should().Be(details.AssignedOwnerId, at);
            details.AssigneeOwnerFingerprint.Should().Be(details.AssignedOwnerFingerprint, at);
            list.AssigneeOwnerId.Should().Be(aggregate.AssigneeOwnerId, at);
            details.AssigneeOwnerId.Should().Be(aggregate.AssigneeOwnerId, at);
        }
    }

    [Fact]
    public void Laying_hold_of_a_draft_moves_only_the_assignee_and_leaves_it_a_draft_nothing_can_claim()
    {
        TaskAggregate aggregate = new();
        aggregate.Apply(Draft());
        TaskAssigneeSet set = new(TaskId, OwnerB, RootB, Now, OwnerA);
        aggregate.Apply(set);

        aggregate.State.Should().Be(TaskState.Draft);
        aggregate.AssigneeOwnerId.Should().Be(OwnerB);
        aggregate.AssigneeOwnerFingerprint.Should().Be(RootB);
        aggregate.AssignedOwnerId.Should().BeNull("queued-for is the go signal and nothing queued it");
        aggregate.AssignedOwnerFingerprint.Should().BeNull();

        TaskListItem list = ApplyToProjection(
            new TaskListItemProjection(), new TaskListItemProjection().Create(new FakeEvent<TaskAdded>(Draft())), set);
        list.State.Should().Be(TaskState.Draft);
        list.AssigneeOwnerId.Should().Be(OwnerB);
        list.AssignedOwnerId.Should().BeNull();

        TaskDetails details = ApplyToProjection(
            new TaskDetailsProjection(), new TaskDetailsProjection().Create(new FakeEvent<TaskAdded>(Draft())), set);
        details.AssigneeOwnerFingerprint.Should().Be(RootB);
        details.AssignedOwnerId.Should().BeNull();

        Action claim = () => TaskDecider.Claim(aggregate, NodeB, OwnerB, DomainId.New(), Now, RootB);
        claim.Should().Throw<DomainConflictException>("a Draft is never claimable, however it is held");
    }

    [Fact]
    public void Letting_go_of_a_draft_returns_it_to_nobody_and_a_later_go_signal_names_the_same_owner_in_both_fields()
    {
        TaskAggregate aggregate = new();
        aggregate.Apply(Draft());
        aggregate.Apply(new TaskAssigneeSet(TaskId, OwnerB, RootB, Now, OwnerA));
        aggregate.Apply(new TaskAssigneeCleared(TaskId, null, Now, OwnerB));
        aggregate.AssigneeOwnerId.Should().BeNull();
        aggregate.AssigneeOwnerFingerprint.Should().BeNull();

        aggregate.Apply(new TaskAssigneeSet(TaskId, OwnerB, RootB, Now, OwnerA));
        aggregate.Apply(Published());
        aggregate.Apply(Assigned(OwnerA, RootA));

        aggregate.AssignedOwnerId.Should().Be(OwnerA);
        aggregate.AssigneeOwnerId.Should().Be(OwnerA, "the go signal also lays hold of the task, replacing a hold set before it");
        aggregate.AssigneeOwnerFingerprint.Should().Be(RootA);
    }

    [Fact]
    public void An_assignee_event_on_a_task_queued_for_someone_cannot_split_the_two_fields()
    {
        TaskAggregate aggregate = new();
        aggregate.Apply(Draft());
        aggregate.Apply(Published());
        aggregate.Apply(Assigned(OwnerA, RootA));

        aggregate.Apply(new TaskAssigneeSet(TaskId, OwnerB, RootB, Now, OwnerA));
        aggregate.AssigneeOwnerId.Should().Be(OwnerA);
        aggregate.Apply(new TaskAssigneeCleared(TaskId, null, Now, OwnerA));
        aggregate.AssigneeOwnerId.Should().Be(OwnerA);
        aggregate.AssigneeOwnerFingerprint.Should().Be(RootA);

        aggregate.Apply(new TaskUnassigned(TaskId, null, Now, OwnerA));
        aggregate.AssigneeOwnerId.Should().BeNull("a queued task releases its assignee through TaskUnassigned");
    }

    [Fact]
    public void Setting_the_assignee_on_a_draft_or_a_published_task_is_accepted_and_naming_the_current_one_is_a_no_op()
    {
        TaskAggregate draft = new();
        draft.Apply(Draft());
        TaskAssigneeSet? first = TaskDecider.SetAssignee(draft, OwnerA, RootA, assigneeIsActor: true, Now, OwnerA);
        first.Should().NotBeNull();
        draft.Apply(first!);
        TaskDecider.SetAssignee(draft, OwnerA, RootA, assigneeIsActor: true, Now, OwnerA)
            .Should().BeNull("the owner already holds it, so nothing is appended");

        draft.Apply(Published());
        TaskDecider.SetAssignee(draft, OwnerB, RootB, assigneeIsActor: false, Now, OwnerA)
            .Should().NotBeNull("a Published task that is not queued can be handed off");
    }

    [Fact]
    public void A_hand_off_on_a_queued_task_is_refused_and_names_the_dequeue_that_takes_it_out_of_the_queue()
    {
        TaskAggregate queued = new();
        queued.Apply(Draft());
        queued.Apply(Published());
        queued.Apply(Assigned(OwnerA, RootA));

        Action handOff = () => TaskDecider.SetAssignee(queued, OwnerB, RootB, assigneeIsActor: false, Now, OwnerA);

        handOff.Should().Throw<DomainConflictException>().WithMessage($"*h9k task dequeue {TaskId}*");
    }

    [Fact]
    public void Naming_another_member_on_a_draft_that_never_leaves_the_owners_fleet_is_refused_and_names_share()
    {
        TaskAggregate draft = new();
        draft.Apply(Draft());
        draft.Apply(new TaskScopeSet(TaskId, ReplicationScope.Fleet, Now, OwnerA));

        Action another = () => TaskDecider.SetAssignee(draft, OwnerB, RootB, assigneeIsActor: false, Now, OwnerA);

        another.Should().Throw<DomainConflictException>().WithMessage($"*h9k task share {TaskId}*");
        TaskDecider.SetAssignee(draft, OwnerA, RootA, assigneeIsActor: true, Now, OwnerA)
            .Should().NotBeNull("laying hold of it yourself never needs a teammate to receive the stream");
    }

    [Fact]
    public void Clearing_an_assignee_needs_a_draft_or_published_task_that_has_one()
    {
        TaskAggregate draft = new();
        draft.Apply(Draft());
        Action nothingToClear = () => TaskDecider.ClearAssignee(draft, null, Now, OwnerA);
        nothingToClear.Should().Throw<DomainConflictException>().WithMessage("*no assignee*");

        draft.Apply(new TaskAssigneeSet(TaskId, OwnerA, RootA, Now, OwnerA));
        TaskDecider.ClearAssignee(draft, "not mine after all", Now, OwnerA).Reason.Should().Be("not mine after all");

        draft.Apply(Published());
        draft.Apply(Assigned(OwnerA, RootA));
        Action queued = () => TaskDecider.ClearAssignee(draft, null, Now, OwnerA);
        queued.Should().Throw<DomainConflictException>().WithMessage($"*h9k task unassign {TaskId}*");
    }

    [Fact]
    public void A_published_task_is_held_by_another_owner_only_when_its_assignee_is_not_the_asking_owner()
    {
        TaskAggregate published = new();
        published.Apply(Draft());
        published.Apply(Published());
        TaskDecider.IsHeldByAnotherOwner(published, OwnerB, RootB).Should().BeFalse("nobody holds it");

        published.Apply(new TaskAssigneeSet(TaskId, OwnerA, RootA, Now, OwnerA));
        TaskDecider.IsHeldByAnotherOwner(published, OwnerB, RootB).Should().BeTrue();
        TaskDecider.IsHeldByAnotherOwner(published, OwnerA, RootA).Should().BeFalse();
        TaskDecider.IsHeldByAnotherOwner(published, OwnerA, "a-different-root-on-the-same-owner-id")
            .Should().BeTrue("a recorded root fingerprint alone decides, as it does for the dispatcher");
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
            // An event neither read model reads (a claim, for one) changes nothing on either.
        }

        return view;
    }
}
