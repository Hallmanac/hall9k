using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskAggregate.Apply(TaskBranchPushed)"/> and <see cref="TaskDetailsProjection"/>'s
/// own twin overwrite <see cref="TaskAggregate.LastPushedBranch"/>/<see cref="TaskAggregate.LastPushedBranchTip"/>
/// on every push, which is exactly what <see cref="Hall9k.Daemon.Execution.ForceWithLeasePusher"/>'s
/// recorded-tip door reads before a later push (origin incident, 2026-09-15; see
/// <see cref="Events.TaskBranchPushed"/>'s own doc). Neither Apply method had a test of its own
/// before this (independent pre-PR review, cycle 1, conformance lens).
/// </summary>
public sealed class TaskBranchPushedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Branch = "task/abc12345-do-the-thing";

    [Fact]
    public void Task_aggregate_records_the_branch_and_tip_a_push_landed_at()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Do the thing",
            acceptanceCriteria: ["it is done"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New()));

        task.LastPushedBranch.Should().BeNull("nothing has pushed this task's branch yet");
        task.LastPushedBranchTip.Should().BeNull();

        task.Apply(new TaskBranchPushed(task.Id, Branch, "aaaaaaa1111111111111111111111111111111", Now));

        task.LastPushedBranch.Should().Be(Branch);
        task.LastPushedBranchTip.Should().Be("aaaaaaa1111111111111111111111111111111");

        // A later push overwrites both fields rather than accumulating history: only the
        // most recent tip is ever a candidate for the guard's recorded-tip door.
        task.Apply(new TaskBranchPushed(task.Id, Branch, "bbbbbbb2222222222222222222222222222222", Now.AddMinutes(5)));

        task.LastPushedBranch.Should().Be(Branch);
        task.LastPushedBranchTip.Should().Be("bbbbbbb2222222222222222222222222222222",
            "the guard's recorded-tip door only ever needs to recognize this node's own most recent push");
    }

    [Fact]
    public void Task_details_projection_mirrors_the_aggregates_own_recorded_tip()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Chore,
            null, null, null, Now, DomainId.New())));

        view.LastPushedBranch.Should().BeNull();
        view.LastPushedBranchTip.Should().BeNull();

        projection.Apply(new FakeEvent<TaskBranchPushed>(new TaskBranchPushed(
            id, Branch, "aaaaaaa1111111111111111111111111111111", Now)), view);

        view.LastPushedBranch.Should().Be(Branch);
        view.LastPushedBranchTip.Should().Be("aaaaaaa1111111111111111111111111111111");

        projection.Apply(new FakeEvent<TaskBranchPushed>(new TaskBranchPushed(
            id, Branch, "bbbbbbb2222222222222222222222222222222", Now.AddMinutes(5))), view);

        view.LastPushedBranch.Should().Be(Branch);
        view.LastPushedBranchTip.Should().Be("bbbbbbb2222222222222222222222222222222",
            "PullRequestOpener reads this projection back, not the aggregate, before its own next push");
    }
}
