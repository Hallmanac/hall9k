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
/// mirrors <c>TaskWorkClaimTests</c>'s own shape exactly: warning and proceeding on acknowledgment
/// instead of refusing outright was h9k task start's own behavior first (task 8a56af78-h9k), and
/// this task (0ac72cb8-h9k) converted h9k task work's identical Published entry to the same shape,
/// so the two commands now behave identically at this edge rather than one differing from the
/// other. <see cref="TaskStartCommand.PrepareDeliberateClaimFromBlocked"/>'s own sibling composition, for
/// the already-Blocked entry (task 0ac72cb8-h9k, closing the gap task 8a56af78-h9k deliberately
/// left open), is pinned in the second half of this file — it mirrors <c>TaskWorkClaimTests</c>'s
/// identical Blocked-entry tests, carry-forward case included: that reasoning ("no re-entry branch
/// the way h9k task work has one") was about re-entering a live claim, which this command still
/// never does, not about withholding a carried-forward acknowledgment from a fresh one.
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
    /// h9k task start's own atomic entry (task 8a56af78-h9k) refuses an open dependency without
    /// the acknowledgment flag, naming the blockers rather than merely a count, and pointing at the
    /// override rather than only at h9k task assign — h9k task work's own identical atomic entry
    /// converted to this same shape only later (task 688a1ccf-h9k), so this is no longer a behavior
    /// unique to h9k task start, just the one it had first.
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

    [Fact]
    public void A_blocked_task_with_no_acknowledgment_is_refused_and_names_the_blocker()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);

        Action act = () => TaskStartCommand.PrepareDeliberateClaimFromBlocked(
            task, Owner, [open], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*Blocked*")
            .Where(exception => exception.Message.Contains(open.Describe())
                    && exception.Message.Contains("--acknowledge-unmet-dependencies"),
                "the refusal names the open blocker and the override flag");

        task.State.Should().Be(TaskState.Blocked, "the refusal decides nothing");
    }

    [Fact]
    public void A_blocked_task_with_a_fresh_acknowledgment_claims_and_records_it_as_not_carried_forward()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);
        Guid runId = DomainId.New();

        (TaskClaimed claimed, bool carriedForward) = TaskStartCommand.PrepareDeliberateClaimFromBlocked(
            task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: true);

        carriedForward.Should().BeFalse("the flag was passed fresh this time, not carried from an earlier claim");
        claimed.NodeId.Should().Be(Guid.Empty);
        claimed.RunId.Should().Be(runId);
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeFalse();
    }

    /// <summary>
    /// The carry-forward this task (0ac72cb8-h9k) adds to h9k task start's own Blocked entry,
    /// exactly as h9k task work's identical entry already has it (design ruling R7): once an
    /// earlier deliberate claim on this same task acknowledged this exact blocker (recorded on
    /// <see cref="TaskAggregate.AcknowledgedUnmetDependencyIds"/>, which a handback or a retry does
    /// not clear), a later start of the identical still-open blocker does not need the flag again
    /// and is recorded as relying on that earlier acknowledgment.
    /// </summary>
    [Fact]
    public void A_blocked_task_already_acknowledged_by_an_earlier_deliberate_claim_does_not_need_the_flag_again()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);
        task.Apply(TaskDecider.ClaimDeliberately(
            task, Owner, DomainId.New(), Now, dependencyOverrideAcknowledged: true));
        task.Apply(TaskDecider.HandBack(task, task.CurrentRunId!.Value, "task/x-y", "handing back", Now, Owner));
        task.State.Should().Be(TaskState.Blocked, "the same still-open blocker is on record unmet");
        task.UnmetDependenciesAlreadyAcknowledged.Should().BeTrue();

        Guid runId = DomainId.New();
        (TaskClaimed claimed, bool carriedForward) = TaskStartCommand.PrepareDeliberateClaimFromBlocked(
            task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: false);

        carriedForward.Should().BeTrue("this exact blocker was already acknowledged by the earlier claim");
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeTrue();
    }

    private static TaskAggregate BlockedTask(TaskDependency open)
    {
        TaskAggregate task = PublishedTask(open.Id);
        task.Apply(TaskDecider.Assign(task, Owner, [open], Now, Owner));
        task.State.Should().Be(TaskState.Blocked);
        return task;
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
