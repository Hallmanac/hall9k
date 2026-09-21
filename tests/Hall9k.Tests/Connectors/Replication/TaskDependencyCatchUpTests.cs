using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Task 9eb5b245: a task pulled from a peer may name blockers whose own streams never came with
/// it, and <c>h9k task assign</c> then refuses the assignment for a dependency the platform could
/// have fetched. This is the walk that finds those, as a pure function over the edges this node
/// holds — no database, no daemon, no second node.
/// </summary>
public sealed class TaskDependencyCatchUpTests
{
    private static readonly Guid Project = DomainId.New();
    private static readonly Guid OtherProject = DomainId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_landed_task_whose_blocker_is_not_here_names_it_and_says_which_task_named_it()
    {
        Guid landed = DomainId.New();
        Guid blocker = DomainId.New();

        IReadOnlyList<TaskDependencyCatchUp.MissingDependency> missing = TaskDependencyCatchUp.MissingDependencies(
            [landed], Project, Held(Edges(landed, blockedBy: [blocker])));

        missing.Should().ContainSingle();
        missing[0].StreamId.Should().Be(blocker);
        missing[0].NamedByTaskId.Should().Be(landed, "h9k status says whose dependency it is fetching");
    }

    [Fact]
    public void A_stacked_on_parent_counts_exactly_as_a_blocker_does()
    {
        Guid child = DomainId.New();
        Guid parent = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies([child], Project, Held(Edges(child, stackedOn: parent)))
            .Select(missing => missing.StreamId)
            .Should().Equal([parent], "a stacked child's branch is cut from its parent's, so the parent is needed too");
    }

    [Fact]
    public void Nothing_is_asked_for_when_every_dependency_is_already_here()
    {
        Guid landed = DomainId.New();
        Guid blocker = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies(
                [landed], Project, Held(Edges(landed, blockedBy: [blocker]), Edges(blocker)))
            .Should().BeEmpty();
    }

    /// <summary>The walk descends through dependencies this node HOLDS, since a stream that is not
    /// here has no edges to read: a blocker's own blocker is found in the same pass.</summary>
    [Fact]
    public void The_walk_descends_through_a_held_dependency_to_its_own_missing_one()
    {
        Guid landed = DomainId.New();
        Guid heldBlocker = DomainId.New();
        Guid deeperBlocker = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies(
                [landed],
                Project,
                Held(Edges(landed, blockedBy: [heldBlocker]), Edges(heldBlocker, blockedBy: [deeperBlocker])))
            .Select(missing => missing.StreamId)
            .Should().Equal([deeperBlocker]);
    }

    [Fact]
    public void One_stream_two_tasks_name_is_asked_for_once()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid sharedBlocker = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies(
                [first, second],
                Project,
                Held(Edges(first, blockedBy: [sharedBlocker]), Edges(second, blockedBy: [sharedBlocker])))
            .Should().ContainSingle("a diamond in the graph is one missing stream, not two asks for it");
    }

    [Fact]
    public void A_cycle_among_held_tasks_terminates_rather_than_walking_forever()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid missing = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies(
                [first],
                Project,
                Held(Edges(first, blockedBy: [second]), Edges(second, blockedBy: [first, missing])))
            .Select(candidate => candidate.StreamId)
            .Should().Equal([missing], "a graph under construction may reference itself in Draft (Decisions Log #34)");
    }

    /// <summary>A request has to name a project, and the project of a stream this node does not hold
    /// is unknowable — so the walk stays inside the project whose own landing triggered it rather
    /// than asking one project's members for another project's stream.</summary>
    [Fact]
    public void The_walk_never_reads_edges_off_a_task_in_another_project()
    {
        Guid landed = DomainId.New();
        Guid foreignBlocker = DomainId.New();
        Guid blockerOfTheForeignOne = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies(
                [landed],
                Project,
                Held(
                    Edges(landed, blockedBy: [foreignBlocker]),
                    Edges(foreignBlocker, blockedBy: [blockerOfTheForeignOne], projectId: OtherProject)))
            .Should().BeEmpty("the blocker itself is held, and its own edges belong to a project this ask is not about");
    }

    [Fact]
    public void A_landed_stream_that_is_not_a_task_this_node_holds_contributes_nothing()
    {
        Guid runStream = DomainId.New();

        TaskDependencyCatchUp.MissingDependencies([runStream], Project, Held())
            .Should().BeEmpty("a read applies whatever a batch carried; only the edges of held TASKS name a dependency");
    }

    /// <summary>
    /// The link the whole feature exists for: the ids <c>TaskDecider.Assign</c> refuses an
    /// assignment over are exactly the ids this walk asks for, so no assignment is ever refused for
    /// a dependency the platform could have fetched and did not.
    /// </summary>
    [Fact]
    public void Every_dependency_the_assign_refusal_names_is_one_this_walk_asks_for()
    {
        Guid blocker = DomainId.New();
        TaskAggregate task = new();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        task.Apply(TaskDecider.Add(
            taskId, Project, "Pulled from a peer", ["it works"], TaskType.Feature, null, null, null, Now, ownerId,
            blockedBy: [blocker]));
        task.Apply(new TaskPublished(taskId, Now, ownerId));

        Action assign = () => TaskDecider.Assign(task, ownerId, [], Now, ownerId);

        assign.Should().Throw<DomainNotFoundException>()
            .Which.Message.Should().Contain(blocker.ToString(), "this is the refusal the feature answers");

        TaskDependencyCatchUp.MissingDependencies([taskId], Project, Held(Edges(taskId, blockedBy: [blocker])))
            .Select(missing => missing.StreamId)
            .Should().Equal([blocker]);
    }

    private static TaskDependencyCatchUp.DependencyEdges Edges(
        Guid taskId, IReadOnlyList<Guid>? blockedBy = null, Guid? stackedOn = null, Guid? projectId = null) =>
        new(taskId, projectId ?? Project, blockedBy ?? [], stackedOn);

    private static Dictionary<Guid, TaskDependencyCatchUp.DependencyEdges> Held(
        params TaskDependencyCatchUp.DependencyEdges[] edges) =>
        edges.ToDictionary(entry => entry.TaskId);
}
