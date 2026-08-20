using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task development and task dispatch as two separate lifecycles (Decisions Log #34).
/// Draft is where a task is developed, Published is the readiness gate, and assignment is the
/// go signal: each edge is an explicit act, and the guards here are what make the promise each
/// state carries true.
/// </summary>
public sealed class TaskLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid OtherOwner = DomainId.New();

    [Fact]
    public void Add_creates_a_draft_from_a_project_and_an_objective_alone()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Separate development from dispatch",
            acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner);

        TaskAggregate task = new();
        task.Apply(added);

        task.State.Should().Be(TaskState.Draft, "creation is identity, not readiness");
        task.AssignedOwnerId.Should().BeNull("nothing dispatches until a human assigns it");
    }

    [Fact]
    public void A_draft_is_invisible_to_the_dispatcher_even_though_it_has_a_real_id()
    {
        TaskAggregate task = Draft();

        Action act = () => TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>().WithMessage("*Draft, not Queued*");
    }

    [Fact]
    public void Publish_enforces_the_readiness_contract_and_says_how_to_satisfy_it()
    {
        TaskAggregate task = Draft(criteria: []);

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*acceptance criterion*")
            .WithMessage("*h9k task revise*", "the message has to be self-correcting");
    }

    [Fact]
    public void A_published_task_is_assignable_but_not_claimable_and_not_editable()
    {
        TaskAggregate task = Published();

        task.State.Should().Be(TaskState.Published);
        FluentActions.Invoking(() => TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now))
            .Should().Throw<DomainConflictException>("publishing is the quality gate, not the go signal");
        FluentActions.Invoking(() => Revise(task, objective: "Something else"))
            .Should().Throw<DomainConflictException>()
            .WithMessage("*h9k task draft*", "the revert is the way back to editable");
    }

    [Fact]
    public void A_revision_touches_only_what_it_was_given()
    {
        TaskAggregate task = Draft();
        Guid dependency = DomainId.New();

        task.Apply(TaskDecider.Revise(
            task,
            objective: Optional<string>.Of("Sharper objective"),
            acceptanceCriteria: Optional<IReadOnlyList<string>>.None,
            agentContext: Optional<string>.None,
            blockedBy: Optional<IReadOnlyList<Guid>>.Of([dependency]),
            type: Optional<TaskType>.None,
            model: Optional<AgentModel>.None,
            Now, Owner));

        task.Objective.Should().Be("Sharper objective");
        task.BlockedBy.Should().Equal(dependency);
        task.AcceptanceCriteria.Should().ContainSingle()
            .Which.Should().Be("it is done", "an untouched field is left alone, not retyped");
        task.Type.Should().Be(TaskType.Feature);
    }

    [Fact]
    public void A_revision_that_revises_nothing_is_refused_rather_than_recorded()
    {
        TaskAggregate task = Draft();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*something to revise*");
    }

    [Fact]
    public void The_edit_after_the_fact_path_is_unassign_then_draft_then_revise_then_publish_then_assign()
    {
        TaskAggregate task = Queued();

        FluentActions.Invoking(() => TaskDecider.ReturnToDraft(task, null, Now, Owner))
            .Should().Throw<DomainConflictException>()
            .WithMessage("*unassign it first*", "a task the dispatcher can see is never one keystroke from editable");

        task.Apply(TaskDecider.Unassign(task, "The criteria missed a case", leaseHeld: false, Now, Owner));
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();

        task.Apply(TaskDecider.ReturnToDraft(task, null, Now, Owner));
        task.State.Should().Be(TaskState.Draft);

        task.Apply(Revise(task, objective: "Now with the migration case"));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));

        task.State.Should().Be(TaskState.Queued);
        task.Objective.Should().Be("Now with the migration case");
    }

    [Fact]
    public void Unassign_is_refused_while_a_node_holds_the_lease()
    {
        TaskAggregate task = Queued();

        Action act = () => TaskDecider.Unassign(task, null, leaseHeld: true, Now, Owner);

        act.Should().Throw<DomainConflictException>().WithMessage("*leased by a node right now*");
    }

    [Fact]
    public void Assignment_is_the_only_way_a_task_becomes_claimable_and_only_by_its_owners_nodes()
    {
        TaskAggregate task = Queued();

        FluentActions.Invoking(() => TaskDecider.Claim(task, DomainId.New(), OtherOwner, DomainId.New(), Now))
            .Should().Throw<DomainConflictException>()
            .WithMessage("*claims only its own owner's work*");

        TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now)
            .Should().NotBeNull("the assigned owner's node claims it exactly as before");
    }

    [Fact]
    public void Assigning_with_an_open_dependency_blocks_instead_of_queueing()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);

        TaskAssigned assigned = TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Done, closedOut: false)], Now, Owner);
        task.Apply(assigned);

        task.State.Should().Be(
            TaskState.Blocked, "Done is not closed out — the pull request has not merged yet");
        task.UnmetDependencies.Should().Equal(dependencyId);
    }

    [Fact]
    public void A_dependency_at_true_closeout_does_not_block()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);

        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Done, closedOut: true)], Now, Owner));

        task.State.Should().Be(TaskState.Queued);
        task.UnmetDependencies.Should().BeEmpty();
    }

    [Fact]
    public void The_last_dependency_closing_out_moves_the_task_from_blocked_to_queued()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        TaskAggregate task = Published(first, second);
        task.Apply(TaskDecider.Assign(
            task,
            Owner,
            [Dependency(first, TaskState.Queued, closedOut: false), Dependency(second, TaskState.Claimed, closedOut: false)],
            Now,
            Owner));

        task.Apply(TaskDecider.DependencyCompleted(task, first, Now));
        task.State.Should().Be(TaskState.Blocked, "one blocker is still open");

        task.Apply(TaskDecider.DependencyCompleted(task, second, Now));
        task.State.Should().Be(TaskState.Queued, "the ready set is what dependencies shape");
    }

    [Fact]
    public void A_dead_dependency_holds_the_task_for_a_human_rather_than_unblocking_or_stranding_it()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Failed, closedOut: false)], Now, Owner));

        task.Apply(TaskDecider.DependencyFailed(task, dependencyId, "It failed and will not close out.", Now));

        task.State.Should().Be(TaskState.Blocked, "it must not silently become claimable");
        task.DependencyFailureReason.Should().Contain("will not close out", "and it must not silently go quiet");
        FluentActions.Invoking(() => TaskDecider.DependencyFailed(task, dependencyId, "Again.", Now))
            .Should().Throw<DomainConflictException>("repeating one observation tells the human nothing new");
    }

    [Fact]
    public void A_dead_dependency_that_is_retried_and_finishes_clears_the_hold()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Failed, closedOut: false)], Now, Owner));
        task.Apply(TaskDecider.DependencyFailed(task, dependencyId, "It failed.", Now));

        task.Apply(TaskDecider.DependencyCompleted(task, dependencyId, Now.AddHours(1)));

        task.State.Should().Be(TaskState.Queued);
        task.DependencyFailureReason.Should().BeNull("nothing is dead once the blocker actually merged");
    }

    [Fact]
    public void Abandon_reaches_a_draft_and_a_published_task_as_well_as_a_run_that_failed()
    {
        TaskDecider.Abandon(Draft(), "Stopped believing in it", Now, Owner).Should().NotBeNull();
        TaskDecider.Abandon(Published(), "Superseded", Now, Owner).Should().NotBeNull();
    }

    [Fact]
    public void A_task_cannot_depend_on_itself()
    {
        Guid id = DomainId.New();

        Action act = () => TaskDecider.Add(
            id, DomainId.New(), "Wait for me", ["done"], TaskType.Feature,
            null, null, null, Now, Owner, blockedBy: [id]);

        act.Should().Throw<DomainValidationException>().WithMessage("*cannot depend on itself*");
    }

    [Fact]
    public void Publishing_a_task_whose_dependency_is_unknown_is_refused_by_name()
    {
        Guid ghost = DomainId.New();
        TaskAggregate task = Draft(blockedBy: [ghost]);

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainNotFoundException>().WithMessage($"*{ghost}*");
    }

    private static TaskDependency Dependency(Guid id, TaskState state, bool closedOut) =>
        new(id, "A blocker", state, closedOut, CurrentRunState: null, []);

    private static TaskRevised Revise(TaskAggregate task, string objective) => TaskDecider.Revise(
        task, Optional<string>.Of(objective), Optional<IReadOnlyList<string>>.None, Optional<string>.None,
        Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner);

    private static TaskAggregate Draft(IReadOnlyList<string>? criteria = null, IReadOnlyList<Guid>? blockedBy = null)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Separate development from dispatch",
            criteria ?? ["it is done"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy));
        return task;
    }

    private static TaskAggregate Published(params Guid[] blockedBy)
    {
        TaskAggregate task = Draft(blockedBy: blockedBy);
        TaskDependencyGraph graph = new(
            [.. blockedBy.Select(id => Dependency(id, TaskState.Queued, closedOut: false))]);
        task.Apply(TaskDecider.Publish(task, graph, Now, Owner));
        return task;
    }

    private static TaskAggregate Queued()
    {
        TaskAggregate task = Published();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }
}
