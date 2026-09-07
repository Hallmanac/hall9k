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
/// The stacked edge as a declaration and as a rule (task: a stacked pull-request edge exists as an
/// explicit opt-in dependency). Two things are load-bearing here and both are one-directional
/// hazards: a stacked edge the tool inferred rather than a human declaring it would put unrelated
/// work on a shared branch (Brian's cohesion ruling, 2026-08-28), and a stacked edge whose
/// dependency edge went missing would be invisible to the publish-time cycle walk and to the
/// unmet-set bookkeeping — the invariant this whole feature rests on.
/// </summary>
public sealed class StackedEdgeTests
{
    private const string PullRequest = "https://github.com/x/y/pull/7";

    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void A_task_declares_no_stacked_edge_by_default()
    {
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: []));

        task.StackedOnTaskId.Should().BeNull(
            "the tool never infers a stack from an ordinary dependency — the edge is always declared");
    }

    [Fact]
    public void An_ordinary_blocked_by_is_never_read_as_a_stacked_edge()
    {
        Guid blockerId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [blockerId]));

        task.StackedOnTaskId.Should().BeNull();
        task.IsStackedOn(blockerId).Should().BeFalse("a plain blocked-by task behaves exactly as before");
    }

    [Fact]
    public void A_declared_stacked_edge_is_recorded_and_answers_for_that_blocker_alone()
    {
        Guid parentId = DomainId.New();
        Guid otherBlockerId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId, otherBlockerId], stackedOn: parentId));

        task.StackedOnTaskId.Should().Be(parentId);
        task.IsStackedOn(parentId).Should().BeTrue();
        task.IsStackedOn(otherBlockerId).Should().BeFalse(
            "a task's other blockers stay ordinary blocked-by edges");
    }

    [Fact]
    public void A_stacked_edge_outside_the_dependency_set_is_refused_with_the_fix()
    {
        Guid parentId = DomainId.New();

        Action declare = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Slice two", ["It works"], TaskType.Feature, null, null, null,
            DateTimeOffset.UtcNow, Owner, blockedBy: [], stackedOnTaskId: parentId);

        declare.Should().Throw<DomainValidationException>()
            .WithMessage("*not among this task's blocked-by set*")
            .WithMessage($"*--stacked-on {parentId}*")
            .WithMessage($"*--blocked-by {parentId}*");
    }

    [Fact]
    public void A_task_cannot_be_stacked_on_itself()
    {
        Guid taskId = DomainId.New();

        Action declare = () => TaskDecider.Add(
            taskId, DomainId.New(), "Slice one", ["It works"], TaskType.Feature, null, null, null,
            DateTimeOffset.UtcNow, Owner, blockedBy: [taskId], stackedOnTaskId: taskId);

        // The self-dependency rule fires first and refuses for its own reason; either refusal is
        // correct, and the point of this test is that the pair never becomes a stack of one.
        declare.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_pr_review_task_cannot_be_stacked_on_anything()
    {
        Guid parentId = DomainId.New();

        Action declare = () => TaskDecider.VetStackedEdge(
            DomainId.New(), parentId, [parentId], TaskType.PrReview);

        declare.Should().Throw<DomainValidationException>()
            .WithMessage("*never opens one of its own*")
            .WithMessage("*Drop --stacked-on*");
    }

    [Fact]
    public void Publishing_refuses_a_stack_on_a_pr_review_parent()
    {
        Guid parentId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));
        TaskDependencyGraph graph = new([
            new TaskDependency(
                parentId, "Review someone else's pull request", TaskState.Done, IsClosedOut: true,
                RunState.Completed, PullRequest, TaskType.PrReview, []),
        ]);

        Action publish = () => TaskDecider.Publish(task, graph, DateTimeOffset.UtcNow, Owner);

        publish.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*which is a pull-request review*")
            .WithMessage("*--clear-stacked-on*");
    }

    /// <summary>
    /// The whole point of the edge: the child starts at the parent's Delivered — pull request open,
    /// internal review complete, branch largely settled — rather than at its merge.
    /// </summary>
    [Fact]
    public void A_stacked_parent_stops_blocking_at_delivered()
    {
        TaskDependency delivered = Dependency(
            TaskState.Done, RunState.AwaitingReview, closedOut: false, PullRequest);

        delivered.Blocks.Should().BeTrue("a plain blocked-by edge still waits for the merge");
        delivered.BlocksStackedChild.Should().BeFalse(
            "the parent's branch is pushed and its pull request open, which is all a stacked child needs");
    }

    [Fact]
    public void A_stacked_parent_still_blocks_before_it_delivers()
    {
        TaskDependency building = Dependency(TaskState.Claimed, RunState.Running, closedOut: false);

        building.BlocksStackedChild.Should().BeTrue(
            "there is no branch on the remote to cut from or target a pull request at yet");
    }

    [Fact]
    public void A_merged_parent_is_past_delivered_rather_than_short_of_it()
    {
        TaskDependency merged = Dependency(
            TaskState.Done, RunState.Completed, closedOut: true, PullRequest);

        merged.BlocksStackedChild.Should().BeFalse();
        merged.Blocks.Should().BeFalse();
    }

    /// <summary>
    /// The narrower death rule. A Done parent whose merge observation will never arrive has still
    /// delivered the branch and pull request its stacked child built on, and that child dispatched
    /// long ago — recording a dead-blocker hold for it would park the child for a hold that never
    /// applied to it.
    /// </summary>
    [Fact]
    public void A_done_parent_that_will_never_close_out_is_not_dead_to_its_stacked_child()
    {
        TaskDependency stranded = Dependency(
            TaskState.Done, RunState.Superseded, closedOut: false, PullRequest);

        stranded.IsDead.Should().BeTrue("a plain blocked-by dependent waits forever on this");
        stranded.IsDeadForStackedChild.Should().BeFalse(
            "it delivered the branch and pull request the stacked child is already built on");
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void A_parent_that_ended_without_delivering_is_dead_to_its_stacked_child_too(string state)
    {
        TaskDependency ended = Dependency(state, RunState.Failed, closedOut: false);

        ended.IsDeadForStackedChild.Should().BeTrue(
            "nothing is coming — a stacked child waiting on it waits forever");
    }

    /// <summary>
    /// The rule holder is what keeps <c>TaskDecider.Assign</c> and <c>TaskDependencyResolver</c>
    /// from ever disagreeing, so it is tested as the seam both go through rather than only through
    /// each of them.
    /// </summary>
    [Fact]
    public void The_rules_apply_the_stacked_bar_to_the_declared_parent_and_the_ordinary_bar_to_everything_else()
    {
        Guid parentId = DomainId.New();
        Guid otherId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId, otherId], stackedOn: parentId));

        TaskDependency parentDelivered = Dependency(
            TaskState.Done, RunState.AwaitingReview, closedOut: false, PullRequest, id: parentId);
        TaskDependency otherDelivered = Dependency(
            TaskState.Done, RunState.AwaitingReview, closedOut: false, PullRequest, id: otherId);

        StackedEdgeRules.Blocks(task, parentDelivered).Should().BeFalse();
        StackedEdgeRules.Blocks(task, otherDelivered).Should().BeTrue(
            "an ordinary blocked-by edge is unchanged by the stacked edge beside it");
        StackedEdgeRules.Blocks(task.StackedOnTaskId, parentDelivered).Should().BeFalse(
            "the projection-side overload must answer identically to the aggregate one");
    }

    /// <summary>
    /// The assignment freezes the unmet set, so a parent already Delivered when the child is
    /// assigned must leave it Queued rather than Blocked — which is what "dispatchable at the
    /// parent's Delivered" actually means at the one moment it is decided.
    /// </summary>
    [Fact]
    public void Assigning_a_stacked_child_behind_a_delivered_parent_leaves_it_queued()
    {
        Guid parentId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));
        task.Apply(TaskDecider.Publish(task, GraphWith(parentId, delivered: true), DateTimeOffset.UtcNow, Owner));

        TaskAssigned assigned = TaskDecider.Assign(
            task, Owner, [Dependency(TaskState.Done, RunState.AwaitingReview, false, PullRequest, id: parentId)],
            DateTimeOffset.UtcNow, Owner);
        task.Apply(assigned);

        assigned.UnmetDependencies.Should().BeEmpty();
        task.State.Should().Be(TaskState.Queued, "the parent has delivered, so the child is dispatchable");
    }

    [Fact]
    public void Assigning_the_same_child_behind_a_parent_that_has_not_delivered_leaves_it_blocked()
    {
        Guid parentId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));
        task.Apply(TaskDecider.Publish(task, GraphWith(parentId, delivered: false), DateTimeOffset.UtcNow, Owner));

        TaskAssigned assigned = TaskDecider.Assign(
            task, Owner, [Dependency(TaskState.Claimed, RunState.Running, false, id: parentId)],
            DateTimeOffset.UtcNow, Owner);
        task.Apply(assigned);

        assigned.UnmetDependencies.Should().Equal(parentId);
        task.State.Should().Be(TaskState.Blocked);
    }

    /// <summary>
    /// An assignment behind a plain blocked-by dependency that has merely reached Delivered must
    /// still land Blocked. This is the "a plain blocked-by task behaves exactly as today" criterion
    /// asserted at the one place the two rules could have been confused.
    /// </summary>
    [Fact]
    public void Assigning_a_plain_dependent_behind_a_delivered_blocker_still_leaves_it_blocked()
    {
        Guid blockerId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [blockerId]));
        task.Apply(TaskDecider.Publish(task, GraphWith(blockerId, delivered: true), DateTimeOffset.UtcNow, Owner));

        TaskAssigned assigned = TaskDecider.Assign(
            task, Owner, [Dependency(TaskState.Done, RunState.AwaitingReview, false, PullRequest, id: blockerId)],
            DateTimeOffset.UtcNow, Owner);
        task.Apply(assigned);

        assigned.UnmetDependencies.Should().Equal(
            [blockerId], "true closeout is still the bar for a plain edge");
        task.State.Should().Be(TaskState.Blocked);
    }

    [Fact]
    public void A_revision_can_declare_the_edge_and_can_drop_it_without_touching_the_dependency()
    {
        Guid parentId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId]));

        TaskRevised declared = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner, stackedOnTaskId: Optional<Guid?>.Of(parentId));
        task.Apply(declared);
        task.StackedOnTaskId.Should().Be(parentId);

        TaskRevised dropped = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner, stackedOnTaskId: Optional<Guid?>.Of(null));
        task.Apply(dropped);

        task.StackedOnTaskId.Should().BeNull();
        task.BlockedBy.Should().Equal(
            [parentId], "the two are separate declarations — dropping the stack leaves the dependency alone");
    }

    /// <summary>
    /// The invariant's own escape hatch, closed: a revision that rewrites the dependency set and
    /// drops the parent out of it would leave the aggregate holding an edge outside its own set,
    /// invisible to the cycle walk and the unmet bookkeeping. Refused rather than silently dropped,
    /// because the human declared the stack and only they can say which declaration is now wrong.
    /// </summary>
    [Fact]
    public void A_revision_that_strands_an_existing_stacked_edge_outside_the_dependency_set_is_refused()
    {
        Guid parentId = DomainId.New();
        Guid replacementId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));

        Action revise = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.Of([replacementId]), Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner);

        revise.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*no longer names it*")
            .WithMessage("*--clear-stacked-on*");
    }

    [Fact]
    public void Clearing_the_stacked_edge_in_the_same_revision_that_drops_the_dependency_is_accepted()
    {
        Guid parentId = DomainId.New();
        Guid replacementId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));

        TaskRevised revised = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.Of([replacementId]), Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner, stackedOnTaskId: Optional<Guid?>.Of(null));
        task.Apply(revised);

        task.StackedOnTaskId.Should().BeNull();
        task.BlockedBy.Should().Equal(replacementId);
    }

    /// <summary>
    /// A revision that leaves a still-valid edge alone must record nothing about it — otherwise a
    /// bare <c>h9k task revise &lt;id&gt;</c> with no options would silently succeed on any stacked
    /// task instead of being refused for having nothing to revise.
    /// </summary>
    [Fact]
    public void A_revision_that_changes_nothing_is_still_refused_on_a_stacked_task()
    {
        Guid parentId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));

        Action revise = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner);

        revise.Should().Throw<DomainValidationException>()
            .WithMessage("*needs something to revise*");
    }

    /// <summary>
    /// The rebase budget is the child's own and is spent only by replays, so a parent that
    /// force-pushes repeatedly cannot consume the child's review budget (the reason
    /// <c>TaskAggregate.StackReplaysDispatched</c> is counted apart from <c>CloseoutAttempts</c>).
    /// </summary>
    [Fact]
    public void A_stacked_replay_spends_the_replay_budget_and_not_the_lifetime_closeout_ceiling()
    {
        TaskAggregate task = DeliveredStackedChild(out Guid parentId);

        task.Apply(new TaskReopened(
            task.Id, task.CurrentRunId!.Value, "task/child", "the parent moved", DateTimeOffset.UtcNow, Owner,
            FollowUpKind.StackReplay, Automatic: true, ObstructionKey: "StackReplay:abc",
            ObstructionSummary: "the parent branch moved", StackReplayUpstreamCommit: "abc123",
            StackReplayOntoCommit: "cafe111"));

        task.StackReplaysDispatched.Should().Be(1);
        task.CloseoutAttempts.Should().Be(0,
            "a replay answers the parent moving, not an obstruction of this task's own");
        task.StackReplayUpstreamCommit.Should().Be("abc123");
        task.StackReplayOntoCommit.Should().Be("cafe111");
        task.LastAutomaticObstructionKey.Should().BeNull(
            "a replay cleared no obstruction of this task's own, so the progress counter is untouched");
        parentId.Should().NotBeEmpty();
    }

    [Fact]
    public void An_ordinary_automatic_follow_up_still_spends_the_lifetime_ceiling()
    {
        TaskAggregate task = DeliveredStackedChild(out Guid _);

        task.Apply(new TaskReopened(
            task.Id, task.CurrentRunId!.Value, "task/child", "checks failing", DateTimeOffset.UtcNow, Owner,
            FollowUpKind.FailingChecks, Automatic: true, ObstructionKey: "FailingChecks:build"));

        task.CloseoutAttempts.Should().Be(1);
        task.StackReplaysDispatched.Should().Be(0);
        task.StackReplayUpstreamCommit.Should().BeNull("only a replay carries one");
    }

    [Fact]
    public void A_manual_reopen_restores_the_replay_budget_alongside_the_closeout_one()
    {
        TaskAggregate task = DeliveredStackedChild(out Guid _);
        // Captured before the first reopen: Apply(TaskReopened) can clear CurrentRunId, so reading
        // it again for the second reopen would read null.
        Guid deliveredRunId = task.CurrentRunId!.Value;
        task.Apply(new TaskReopened(
            task.Id, deliveredRunId, "task/child", "the parent moved", DateTimeOffset.UtcNow, Owner,
            FollowUpKind.StackReplay, Automatic: true, StackReplayUpstreamCommit: "abc123",
            StackReplayOntoCommit: "cafe111"));

        task.Apply(new TaskReopened(
            task.Id, deliveredRunId, "task/child", "another go", DateTimeOffset.UtcNow, Owner,
            FollowUpKind.ReviewFeedback, Automatic: false));

        task.StackReplaysDispatched.Should().Be(0, "h9k pr resolve restores the automatic budgets");
    }

    /// <summary>
    /// A replay needs both commits it rebases between, and neither can be recovered later: without
    /// the upstream it would replay the parent's commits onto a base that already holds them, and
    /// without the landing commit it would have to name a ref and land wherever that ref had
    /// drifted to — on the one follow-up no reviewer ever reads. Refused rather than quietly
    /// degraded into a different operation wearing this kind's name.
    /// </summary>
    [Theory]
    [InlineData(null, "cafe111")]
    [InlineData("abc123", null)]
    [InlineData(null, null)]
    public void A_stacked_replay_reopen_missing_either_of_its_two_commits_is_refused(
        string? upstream, string? onto)
    {
        TaskAggregate task = DeliveredStackedChild(out Guid _);

        Action reopen = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/child", "the parent moved", FollowUpKind.StackReplay,
            automatic: true, DateTimeOffset.UtcNow, Owner,
            stackReplayUpstreamCommit: upstream, stackReplayOntoCommit: onto);

        reopen.Should().Throw<DomainValidationException>()
            .WithMessage("*needs both commits it replays between*");
    }

    [Fact]
    public void A_stacked_replay_reopen_carrying_both_commits_is_accepted()
    {
        TaskAggregate task = DeliveredStackedChild(out Guid _);

        TaskReopened reopened = TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/child", "the parent moved", FollowUpKind.StackReplay,
            automatic: true, DateTimeOffset.UtcNow, Owner,
            stackReplayUpstreamCommit: "abc123", stackReplayOntoCommit: "cafe111");

        reopened.StackReplayUpstreamCommit.Should().Be("abc123");
        reopened.StackReplayOntoCommit.Should().Be("cafe111");
    }

    /// <summary>A Delivered stacked child: added, published, assigned, claimed, and completed onto a pull request.</summary>
    private static TaskAggregate DeliveredStackedChild(out Guid parentId)
    {
        parentId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        TaskAggregate task = new();
        task.Apply(Added(out Guid _, blockedBy: [parentId], stackedOn: parentId));
        task.Apply(TaskDecider.Publish(task, GraphWith(parentId, delivered: true), at, Owner));
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(TaskState.Done, RunState.AwaitingReview, false, PullRequest, id: parentId)],
            at, Owner));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, runId, at));
        task.Apply(TaskDecider.Complete(task, runId, "https://github.com/x/y/pull/8", at));
        return task;
    }

    private static TaskDependencyGraph GraphWith(Guid parentId, bool delivered) => new([
        delivered
            ? Dependency(TaskState.Done, RunState.AwaitingReview, closedOut: false, PullRequest, id: parentId)
            : Dependency(TaskState.Claimed, RunState.Running, closedOut: false, id: parentId),
    ]);

    private static TaskAdded Added(out Guid taskId, IReadOnlyList<Guid> blockedBy, Guid? stackedOn = null)
    {
        taskId = DomainId.New();
        return TaskDecider.Add(
            taskId, DomainId.New(), "Slice two of one idea", ["It works"], TaskType.Feature, null, null, null,
            DateTimeOffset.UtcNow, Owner, blockedBy: blockedBy, stackedOnTaskId: stackedOn);
    }

    private static TaskDependency Dependency(
        TaskState state, RunState? currentRunState, bool closedOut, string? pullRequestUrl = null,
        Guid? id = null) =>
        new(id ?? DomainId.New(), "The parent slice", state, closedOut, currentRunState, pullRequestUrl,
            TaskType.Feature, []);
}
