using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The per-task human gate flag (<c>h9k task add --human-gate</c>, <c>h9k task revise --human-gate</c> and
/// <c>--clear-human-gate</c>): where it can be set, where it is refused, and that a task without it is
/// untouched.
/// </summary>
public sealed class HumanGateTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("01a19510-0000-7000-8000-000000000001");
    private static readonly Guid NodeId = Guid.Parse("01a19510-0000-7000-8000-000000000002");

    private static TaskAggregate Draft(TaskType? type = null, bool humanGate = false)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Rework the status pane", ["parked runs list first"],
            type ?? TaskType.Feature, null, null, null, Now, Owner, humanGate: humanGate));
        return task;
    }

    private static TaskAggregate Published(bool humanGate = false)
    {
        TaskAggregate task = Draft(humanGate: humanGate);
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        return task;
    }

    private static TaskAggregate Queued()
    {
        TaskAggregate task = Published();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }

    private static TaskAggregate Claimed()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Claim(task, NodeId, Owner, DomainId.New(), Now));
        return task;
    }

    private static TaskRevised Revise(TaskAggregate task, Optional<bool> humanGate) =>
        TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            humanGate: humanGate);

    [Fact]
    public void A_task_added_with_the_gate_carries_it_and_one_added_without_does_not()
    {
        Draft(humanGate: true).HumanGate.Should().BeTrue();
        Draft().HumanGate.Should().BeFalse("no project or node default exists: a default that is on is the bottleneck");
    }

    [Theory]
    [InlineData("pr-review")]
    [InlineData("spike")]
    public void A_pr_review_task_and_a_spike_refuse_the_gate_because_they_open_no_pull_request(string type)
    {
        Action add = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", [], TaskType.Parse(type), null, null, null, Now, Owner,
            humanGate: true);

        add.Should().Throw<DomainValidationException>().WithMessage("*opens no pull request of its own*");
    }

    [Fact]
    public void Revise_sets_and_clears_the_gate_on_a_draft()
    {
        TaskAggregate task = Draft();

        task.Apply(Revise(task, Optional<bool>.Of(true)));
        task.HumanGate.Should().BeTrue();

        task.Apply(Revise(task, Optional<bool>.Of(false)));
        task.HumanGate.Should().BeFalse();
    }

    [Fact]
    public void Revise_sets_and_clears_the_gate_on_any_live_task_whose_pull_request_has_not_opened_not_only_a_draft()
    {
        foreach (TaskAggregate task in new[] { Published(), Queued(), Claimed() })
        {
            task.Apply(Revise(task, Optional<bool>.Of(true)));
            task.HumanGate.Should().BeTrue($"a {task.State.Value} task has not opened its pull request");

            task.Apply(Revise(task, Optional<bool>.Of(false)));
            task.HumanGate.Should().BeFalse();
        }
    }

    [Fact]
    public void Revise_refuses_the_gate_once_the_pull_request_is_open()
    {
        TaskAggregate task = Claimed();
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/acme/web/pull/7", Now));

        Action set = () => Revise(task, Optional<bool>.Of(true));
        Action clear = () => Revise(task, Optional<bool>.Of(false));

        set.Should().Throw<DomainConflictException>().WithMessage("*pull request is already open*");
        clear.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Revise_refuses_the_gate_on_an_abandoned_task_which_nothing_will_ever_run_again()
    {
        TaskAggregate task = Published();
        task.Apply(TaskDecider.Abandon(task, "walked away", Now, Owner));

        Action set = () => Revise(task, Optional<bool>.Of(true));

        set.Should().Throw<DomainConflictException>().WithMessage("*the human gate would do nothing*");
    }

    [Fact]
    public void Revise_refuses_to_gate_a_task_that_is_a_spike_or_to_turn_a_gated_task_into_one()
    {
        TaskAggregate spike = Draft(TaskType.Spike);
        Action gate = () => Revise(spike, Optional<bool>.Of(true));
        gate.Should().Throw<DomainValidationException>().WithMessage("*opens no pull request*");

        TaskAggregate gated = Draft(humanGate: true);
        Action retype = () => TaskDecider.Revise(
            gated, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.Of(TaskType.Spike), Optional<AgentModel>.None, Now, Owner);
        retype.Should().Throw<DomainValidationException>().WithMessage("*opens no pull request*");
    }

    [Fact]
    public void The_gate_does_not_widen_what_a_published_task_lets_through_beyond_markers()
    {
        TaskAggregate task = Published();

        Action wider = () => TaskDecider.Revise(
            task, Optional<string>.Of("New objective"), Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            humanGate: Optional<bool>.Of(true));

        wider.Should().Throw<DomainConflictException>().WithMessage("*only a draft can be revised*");
    }

    [Fact]
    public void An_evolved_task_records_the_task_it_came_from()
    {
        Guid source = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Rework the status pane", [], TaskType.Feature, null, null, null, Now, Owner,
            humanGate: true, evolvedFromTaskId: source);
        TaskAggregate task = new();
        task.Apply(added);

        task.EvolvedFromTaskId.Should().Be(source);
        task.HumanGate.Should().BeTrue();
    }
}
