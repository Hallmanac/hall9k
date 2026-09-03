using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The atomic decision behind h9k task start's Published entry (task 8a56af78-h9k):
/// <see cref="TaskStartCommand.PrepareDeliberateClaimFromPublished"/> composes
/// <see cref="TaskDecider.Assign"/> and <see cref="TaskDecider.ClaimDeliberately"/> into one unit,
/// with no session and no append, so the composition itself is pinned here without a database —
/// mirrors <c>TaskWorkClaimTests</c>'s own shape exactly, but for the one behavior that differs:
/// an unmet dependency warns and proceeds on acknowledgment instead of refusing outright.
/// </summary>
public sealed class TaskStartClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void A_published_task_with_no_open_dependencies_is_assigned_and_claimed_in_one_unit()
    {
        TaskAggregate task = PublishedTask();
        Guid runId = DomainId.New();

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskStartCommand.PrepareDeliberateClaimFromPublished(
                task, Owner, [], runId, Now, acknowledgeUnmetDependencies: false);

        assigned.AssignedOwnerId.Should().Be(Owner);
        assigned.UnmetDependencies.Should().BeEmpty();
        unmet.Should().BeEmpty();

        claimed.NodeId.Should().Be(Guid.Empty, "a deliberate kick-off carries the sentinel node id, same as an interactive claim");
        claimed.OwnerId.Should().Be(Owner);
        claimed.RunId.Should().Be(runId);
        claimed.LeaseGeneration.Should().Be(1);
        claimed.DependencyOverrideAcknowledged.Should().BeFalse("nothing needed overriding");

        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(Owner);
    }

    [Fact]
    public void A_published_task_with_a_closed_out_dependency_is_still_assigned_and_claimed()
    {
        TaskDependency closed = ClosedDependency();
        TaskAggregate task = PublishedTask(closed.Id);

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskStartCommand.PrepareDeliberateClaimFromPublished(
                task, Owner, [closed], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        assigned.UnmetDependencies.Should().BeEmpty("the one dependency has already closed out");
        unmet.Should().BeEmpty();
        claimed.OwnerId.Should().Be(Owner);
        claimed.DependencyOverrideAcknowledged.Should().BeFalse();
        task.State.Should().Be(TaskState.Queued);
    }

    /// <summary>
    /// The behavior that differs from h9k task work's own atomic entry (task 688a1ccf-h9k): an
    /// open dependency, without the acknowledgment flag, refuses — but names the blockers rather
    /// than merely a count, and points at the override rather than only at h9k task assign.
    /// </summary>
    [Fact]
    public void A_published_task_with_an_open_dependency_and_no_acknowledgment_is_refused_and_names_the_blocker()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = PublishedTask(open.Id);

        Action act = () => TaskStartCommand.PrepareDeliberateClaimFromPublished(
            task, Owner, [open], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s) that have not closed out*")
            .Where(exception => exception.Message.Contains(open.Describe())
                    && exception.Message.Contains("--acknowledge-unmet-dependencies"),
                "the refusal names the open blocker and the exact override flag to re-run with");

        // Nothing about the task was decided — the same "refuse up front" guarantee h9k task
        // work's own atomic entry gives, so a caller that retries once the blocker clears (or
        // with the flag) reads a task still Published.
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
    }

    /// <summary>
    /// The platform advises rather than refuses (the idea's own ruling, fcaded0b): with the
    /// acknowledgment, the same open dependency that refused above instead assigns and claims,
    /// landing Claimed directly rather than Blocked, with the override recorded on the claim.
    /// </summary>
    [Fact]
    public void A_published_task_with_an_open_dependency_and_acknowledgment_is_assigned_and_claimed_anyway()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = PublishedTask(open.Id);
        Guid runId = DomainId.New();

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskStartCommand.PrepareDeliberateClaimFromPublished(
                task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: true);

        assigned.UnmetDependencies.Should().ContainSingle().Which.Should().Be(open.Id);
        unmet.Should().ContainSingle().Which.Should().Be(open);
        claimed.NodeId.Should().Be(Guid.Empty);
        claimed.DependencyOverrideAcknowledged.Should().BeTrue("the human overrode the open dependency deliberately");
        claimed.RunId.Should().Be(runId);

        // Applied in place (mirrors TaskWorkCommand's own PrepareInteractiveClaimFromPublished):
        // the caller's own aggregate reflects the assignment the atomic append is about to
        // commit — still Blocked, since this helper hands the claim back rather than applying it
        // itself, exactly as the sibling test above leaves the no-override case at Queued.
        task.State.Should().Be(TaskState.Blocked);
        task.AssignedOwnerId.Should().Be(Owner);
    }

    /// <summary>
    /// A dead blocker (<see cref="TaskDependency.IsDead"/>) will never close out — the refusal
    /// must not promise it "queues itself the moment the last one's pull request merges" the way
    /// <c>h9k task assign ... to hold it Blocked until they clear</c> would;
    /// <see cref="TaskDependency.DescribeDeath"/>'s honest remedy belongs here instead, reused
    /// from <see cref="TaskWorkCommand.DescribeUnmetDependencyAdvice"/> rather than re-derived —
    /// mirrors <c>TaskWorkClaimTests</c>'s identical case (independent pre-PR review, cycle 1,
    /// adversarial finding at TaskStartCommand.cs:427: this refusal named the blocker but still
    /// made the false promise a dead blocker can never keep).
    /// </summary>
    [Fact]
    public void A_published_task_with_a_dead_dependency_is_refused_without_a_false_merge_promise()
    {
        TaskDependency dead = DeadDependency();
        TaskAggregate task = PublishedTask(dead.Id);

        Action act = () => TaskStartCommand.PrepareDeliberateClaimFromPublished(
            task, Owner, [dead], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s) that have not closed out*")
            .Where(exception => exception.Message.Contains(dead.Describe())
                    && exception.Message.Contains(dead.DescribeDeath())
                    && !exception.Message.Contains("queues itself the moment"),
                "a dead blocker's pull request will never merge, so the ordinary queues-itself "
                + "promise must not be made for it");
    }

    private static TaskDependency DeadDependency() => new(
        DomainId.New(), "A blocker that was abandoned", TaskState.Abandoned, IsClosedOut: false,
        CurrentRunState: null, PullRequestUrl: null, TaskType.Chore, []);

    private static TaskAggregate PublishedTask(params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Prove the deliberate claim composes assign and claim",
            ["one event append, exactly one winner"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy);
        task.Apply(added);

        TaskDependencyGraph graph = blockedBy.Length == 0
            ? TaskDependencyGraph.Empty
            : new TaskDependencyGraph(blockedBy.Select(id => new TaskDependency(
                id, "A blocker", TaskState.Done, IsClosedOut: true, CurrentRunState: null,
                PullRequestUrl: null, TaskType.Chore, [])));
        task.Apply(TaskDecider.Publish(task, graph, Now, Owner));
        return task;
    }

    private static TaskDependency ClosedDependency() => new(
        DomainId.New(), "A blocker already merged", TaskState.Done, IsClosedOut: true,
        CurrentRunState: RunState.Completed, PullRequestUrl: "https://github.com/x/y/pull/1",
        TaskType.Chore, []);

    private static TaskDependency OpenDependency() => new(
        DomainId.New(), "A blocker still running", TaskState.Claimed, IsClosedOut: false,
        CurrentRunState: RunState.Running, PullRequestUrl: null,
        TaskType.Chore, []);
}
