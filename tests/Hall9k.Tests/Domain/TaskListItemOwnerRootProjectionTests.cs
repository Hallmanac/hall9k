using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The roots <see cref="TaskListItem"/> carries so the board can ask <see cref="TaskOwnerRule"/> of a
/// row (task: h9k status shows a viewer their own work): the holder as the aggregate records it and
/// the requester of a pending take. Each is a mirror, so each test replays the same events through
/// the aggregate and the row and asks them to agree, which is what lets the pane and the commands
/// give one answer. The creator is not a column: the root a native genesis is stamped with is
/// written after the inline projection runs, so the board reads it from the stream instead.
/// </summary>
public sealed class TaskListItemOwnerRootProjectionTests
{
    private const string Mine = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Theirs = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_claim_by_a_node_records_the_claimants_root_as_holder_and_an_interactive_claim_does_not()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        TaskListItem view = new();
        TaskAggregate aggregate = new();

        Apply(projection, aggregate, view, new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, DomainId.New(), Now, OwnerRootFingerprint: Theirs));
        view.HolderOwnerRootFingerprint.Should().Be(Theirs);
        view.HolderOwnerRootFingerprint.Should().Be(aggregate.HolderOwnerRootFingerprint);

        // The sentinel an operator's own claim carries: the aggregate records no holder for it, so the
        // row must keep the previous one rather than make this operator the holder.
        Apply(projection, aggregate, view, new TaskClaimed(
            id, Guid.Empty, DomainId.New(), 2, DomainId.New(), Now.AddMinutes(1), OwnerRootFingerprint: Mine));
        view.HolderOwnerRootFingerprint.Should().Be(aggregate.HolderOwnerRootFingerprint);
        view.HolderOwnerRootFingerprint.Should().Be(Theirs);
    }

    [Fact]
    public void A_release_clears_the_holder_and_a_forced_takeover_sets_the_takers()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        TaskListItem view = new();
        TaskAggregate aggregate = new();
        Apply(projection, aggregate, view, new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, DomainId.New(), Now, OwnerRootFingerprint: Theirs));

        Apply(projection, aggregate, view, new TaskHolderReleased(id, Now.AddHours(1)));
        view.HolderOwnerRootFingerprint.Should().BeNull();
        view.HolderOwnerRootFingerprint.Should().Be(aggregate.HolderOwnerRootFingerprint);

        Apply(projection, aggregate, view, new TaskHolderTakenOver(
            id, null, DomainId.New(), DomainId.New(), Mine, "their node is gone", DomainId.New(), Now.AddHours(2)));
        view.HolderOwnerRootFingerprint.Should().Be(Mine);
        view.HolderOwnerRootFingerprint.Should().Be(aggregate.HolderOwnerRootFingerprint);
    }

    [Fact]
    public void A_take_request_records_who_asked_by_root_until_it_is_refused_granted_or_overridden()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        Guid requesterNode = DomainId.New();
        Guid requesterOwner = DomainId.New();
        TaskListItem view = new();
        TaskAggregate aggregate = new();

        Apply(projection, aggregate, view, new TaskTakeRequested(id, requesterNode, requesterOwner, Mine, "need it", Now));
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().Be(Mine);
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().Be(aggregate.PendingTakeRequestedByOwnerFingerprint);

        Apply(projection, aggregate, view, new TaskTakeRefused(id, requesterNode, requesterOwner, "no", Now.AddMinutes(5)));
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().BeNull();
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().Be(aggregate.PendingTakeRequestedByOwnerFingerprint);

        Apply(projection, aggregate, view, new TaskTakeRequested(id, requesterNode, requesterOwner, Mine, "again", Now.AddMinutes(10)));
        Apply(projection, aggregate, view, new TaskHolderReleased(id, Now.AddMinutes(11), requesterNode, requesterOwner, Mine));
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().BeNull("a grant answers the request");
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().Be(aggregate.PendingTakeRequestedByOwnerFingerprint);

        Apply(projection, aggregate, view, new TaskTakeRequested(id, requesterNode, requesterOwner, Mine, "a third time", Now.AddMinutes(20)));
        Apply(projection, aggregate, view, new TaskHolderTakenOver(
            id, null, requesterNode, requesterOwner, Mine, "forced", requesterOwner, Now.AddMinutes(21)));
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().BeNull("a forced takeover answers the request");
        view.PendingTakeRequestedByOwnerRootFingerprint.Should().Be(aggregate.PendingTakeRequestedByOwnerFingerprint);
    }

    [Fact]
    public void The_facts_read_off_a_row_follow_the_same_order_as_the_reader_and_the_creator_matters_only_when_nobody_holds_or_is_assigned()
    {
        TaskListItem row = new() { Id = DomainId.New() };
        Func<Guid, string?> noOwners = _ => null;
        OwnerRootFact creator = OwnerRootFact.Known(Theirs);

        // Holder first, and an unresolvable assignee does not hide it.
        row.HolderOwnerRootFingerprint = Mine;
        row.AssignedOwnerId = DomainId.New();
        TaskOwnerFacts held = TaskListItemOwnerFacts.From(row, noOwners, creator);
        held.Holder.RootFingerprint.Should().Be(Mine);
        held.Assigned.State.Should().Be(OwnerRootFactState.Unresolved);
        held.Creator.State.Should().Be(OwnerRootFactState.Absent, "the creator is read only when both holder and assignee are absent");
        TaskListItemOwnerFacts.NeedsCreator(row).Should().BeFalse();

        row.HolderOwnerRootFingerprint = null;
        row.AssignedOwnerFingerprint = Theirs;
        TaskListItemOwnerFacts.From(row, noOwners, creator).Assigned.RootFingerprint.Should().Be(Theirs);

        row.AssignedOwnerFingerprint = null;
        TaskListItemOwnerFacts.From(row, _ => Mine, creator).Assigned.RootFingerprint
            .Should().Be(Mine, "an assignment by owner id resolves through this node's own owner table");

        row.AssignedOwnerId = null;
        TaskListItemOwnerFacts.NeedsCreator(row).Should().BeTrue();
        TaskListItemOwnerFacts.From(row, noOwners, creator).Creator.RootFingerprint.Should().Be(Theirs);
        TaskListItemOwnerFacts.From(row, noOwners, creator: null).Creator.State
            .Should().Be(OwnerRootFactState.Unresolved, "a creator nobody supplied is unresolved, never someone's");
    }

    [Fact]
    public void A_task_is_the_viewers_when_the_receive_gates_rule_lets_them_act_or_when_they_asked_to_take_it()
    {
        TaskOwnerFacts theirs = new(OwnerRootFact.Known(Theirs), OwnerRootFact.Absent, OwnerRootFact.Absent);
        TaskOwnerFacts unknown = new(OwnerRootFact.Absent, OwnerRootFact.Unresolved, OwnerRootFact.Absent);
        TaskOwnerFacts mine = new(OwnerRootFact.Absent, OwnerRootFact.Known(Mine), OwnerRootFact.Known(Theirs));

        TaskViewerRule.Decide(Mine, mine, null).IsViewers.Should().BeTrue();
        TaskViewerRule.Decide(Mine, theirs, null).IsViewers.Should().BeFalse();
        TaskViewerRule.Decide(Mine, theirs, Mine).IsViewers.Should().BeTrue();
        TaskViewerRule.Decide(Mine, theirs, Theirs).IsViewers.Should().BeFalse();
        TaskViewerRule.Decide(Mine, unknown, null).IsViewers.Should().BeFalse("an owner nobody can name is not the viewer");
        TaskViewerRule.Decide(Mine, unknown, string.Empty).IsViewers.Should().BeFalse();
        TaskViewerRule.Decide(Mine, theirs, null).Owner.OwnerRootFingerprint.Should().Be(Theirs);
    }

    private static void Apply<T>(TaskListItemProjection projection, TaskAggregate aggregate, TaskListItem view, T data)
        where T : notnull
    {
        switch (data)
        {
            case TaskClaimed claimed:
                aggregate.Apply(claimed);
                projection.Apply(new FakeEvent<TaskClaimed>(claimed), view);
                break;
            case TaskHolderReleased released:
                aggregate.Apply(released);
                projection.Apply(new FakeEvent<TaskHolderReleased>(released), view);
                break;
            case TaskHolderTakenOver takenOver:
                aggregate.Apply(takenOver);
                projection.Apply(new FakeEvent<TaskHolderTakenOver>(takenOver), view);
                break;
            case TaskTakeRequested requested:
                aggregate.Apply(requested);
                projection.Apply(new FakeEvent<TaskTakeRequested>(requested), view);
                break;
            case TaskTakeRefused refused:
                aggregate.Apply(refused);
                projection.Apply(new FakeEvent<TaskTakeRefused>(refused), view);
                break;
            default:
                throw new InvalidOperationException($"No applier for {typeof(T).Name}.");
        }
    }
}
