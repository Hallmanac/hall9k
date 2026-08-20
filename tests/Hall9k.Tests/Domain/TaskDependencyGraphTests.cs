using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Cycle detection at publish, and only at publish (Decisions Log #34): a graph under
/// construction may transiently reference itself while it is being authored, but nothing in a
/// cycle can ever run, so a cycle must never become assignable. The refusal names the cycle,
/// because "there is a cycle somewhere" is not something a human can act on.
/// </summary>
public sealed class TaskDependencyGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void An_acyclic_chain_publishes()
    {
        Guid root = DomainId.New();
        Guid middle = DomainId.New();
        Guid leaf = DomainId.New();
        TaskDependencyGraph graph = new([Node(middle, "Middle", leaf), Node(leaf, "Leaf")]);

        graph.FindCycle(root, [middle]).Should().BeNull();
    }

    [Fact]
    public void A_cycle_three_hops_away_is_still_found_and_named()
    {
        Guid root = DomainId.New();
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        TaskDependencyGraph graph = new([Node(first, "First", second), Node(second, "Second", root)]);
        TaskAggregate task = DraftBlockedBy(root, first);

        Action act = () => TaskDecider.Publish(task, graph, Now, Owner);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*cycle*")
            .WithMessage("*First*")
            .WithMessage("*Second*")
            .WithMessage("*this task*", "the human has to see where the loop closes");
    }

    [Fact]
    public void A_two_task_loop_is_a_cycle_as_much_as_a_long_chain_is()
    {
        Guid root = DomainId.New();
        Guid other = DomainId.New();
        TaskDependencyGraph graph = new([Node(other, "The other half", root)]);

        graph.FindCycle(root, [other]).Should().Equal(root, other, root);
    }

    [Fact]
    public void A_diamond_is_walked_once_per_node_rather_than_reported_as_a_cycle()
    {
        // left and right both depend on shared: shared is reached twice, which is a revisit,
        // not a back edge. Colouring only the current path is what tells the two apart.
        Guid root = DomainId.New();
        Guid left = DomainId.New();
        Guid right = DomainId.New();
        Guid shared = DomainId.New();
        TaskDependencyGraph graph = new(
            [Node(left, "Left", shared), Node(right, "Right", shared), Node(shared, "Shared")]);

        graph.FindCycle(root, [left, right]).Should().BeNull();
    }

    [Fact]
    public void Missing_names_exactly_the_ids_the_platform_does_not_know()
    {
        Guid known = DomainId.New();
        Guid ghost = DomainId.New();
        TaskDependencyGraph graph = new([Node(known, "Known")]);

        graph.Missing([known, ghost]).Should().Equal(ghost);
    }

    private static TaskDependency Node(Guid id, string objective, params Guid[] blockedBy) =>
        new(id, objective, TaskState.Queued, IsClosedOut: false, CurrentRunState: null, blockedBy);

    private static TaskAggregate DraftBlockedBy(Guid id, params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            id, DomainId.New(), "The task at the top of the loop", ["it is done"], TaskType.Feature,
            null, null, null, Now, Owner, blockedBy: blockedBy));
        return task;
    }
}
