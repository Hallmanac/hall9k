using FluentAssertions;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The remote form of the stacked edge as a declaration and as a hold (task: a stacked child can
/// stand on a pull request another install owns). Three things are load-bearing, each a
/// one-directional hazard.
/// <list type="bullet">
/// <item>A child whose parent pull request is not open must not dispatch — there is no branch on
/// origin to cut from, so it would land on the project's base carrying none of the parent's
/// work.</item>
/// <item>A child must never hold two parents: one branch sits on top of exactly one other.</item>
/// <item>An unobserved parent must never read as any state at all, so the hold survives a failed
/// look rather than releasing the child on silence.</item>
/// </list>
/// </summary>
public sealed class RemoteStackedEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Owner = DomainId.New();

    private const int ParentNumber = 264;

    [Fact]
    public void A_task_can_declare_a_pull_request_as_its_stacked_parent()
    {
        TaskAggregate task = Declared();

        task.StackedOnPullRequestNumber.Should().Be(ParentNumber);
        task.IsStackedOnRemotePullRequest.Should().BeTrue();
        task.StackedOnTaskId.Should().BeNull("the two forms are alternatives, never companions");
        task.BlockedBy.Should().BeEmpty(
            "a remote parent carries no dependency edge — there is no local task to name");
    }

    [Fact]
    public void An_unobserved_parent_holds_the_child_blocked_at_assignment()
    {
        TaskAggregate task = Assigned();

        task.State.Should().Be(TaskState.Blocked);
        task.AwaitsRemoteStackedParent.Should().BeTrue(
            "nothing has been observed about that pull request, and silence is not evidence it is open");
        task.RemoteStackedParentState.Should().Be(RemoteParentState.Unknown);
        task.RemoteStackedParentHoldReason.Should().BeNull(
            "waiting on a pull request nobody has looked at yet is ordinary waiting, not a human's problem");
    }

    [Fact]
    public void An_open_parent_releases_the_child_to_the_dispatcher()
    {
        TaskAggregate task = Assigned();

        task.Apply(Observed(RemoteParentState.Open, "feature/teammate-slice"));

        task.State.Should().Be(TaskState.Queued,
            "an open pull request IS the remote parent's Delivered (Brian's ruling, 2026-09-07)");
        task.RemoteStackedParentHeadBranch.Should().Be("feature/teammate-slice");
        task.AwaitsRemoteStackedParent.Should().BeFalse();
    }

    /// <summary>
    /// A parent that merged before the child ever dispatched leaves nothing to stack on, so the
    /// child is released as an ordinary task on the project's base — the same reading
    /// <see cref="StackedBaseResolver"/> gives a local parent that has closed out.
    /// </summary>
    [Fact]
    public void A_merged_parent_releases_the_child_too()
    {
        TaskAggregate task = Assigned();

        task.Apply(Observed(RemoteParentState.Merged, "feature/teammate-slice"));

        task.State.Should().Be(TaskState.Queued);
    }

    [Theory]
    [InlineData("Absent")]
    [InlineData("ClosedUnmerged")]
    public void A_parent_that_is_closed_or_absent_holds_the_child(string state)
    {
        TaskAggregate task = Assigned();

        task.Apply(Observed(state, headBranch: string.Empty));

        task.State.Should().Be(TaskState.Blocked, "there is no open pull request to build on");
        task.AwaitsRemoteStackedParent.Should().BeTrue();
    }

    /// <summary>
    /// Slice two's dead-parent rule, one pull request over: only the closed-unmerged case needs a
    /// human. A number declared before the teammate opened the pull request resolves itself.
    /// </summary>
    [Fact]
    public void Only_a_parent_that_closed_unmerged_asks_for_a_human()
    {
        TaskAggregate closed = Assigned();
        closed.Apply(Observed(RemoteParentState.ClosedUnmerged, string.Empty));
        closed.RemoteStackedParentHoldReason.Should().Contain("closed without merging");

        TaskAggregate absent = Assigned();
        absent.Apply(Observed(RemoteParentState.Absent, string.Empty));
        absent.RemoteStackedParentHoldReason.Should().BeNull(
            "a pull request the teammate has not opened yet resolves itself the moment they do");
    }

    /// <summary>
    /// The window between the release and the dispatch, which is where every child sits whenever
    /// the queue is longer than the node's capacity: a parent that stops being open there has to
    /// take the release back with it, or the child cuts from the project's base carrying none of
    /// the parent's work — the hazard this whole feature exists to prevent — while still declaring
    /// the stack and still carrying the hold (independent pre-PR review, cycle 1, adversarial
    /// lens). Nothing has cut a branch yet at that point, so there is nothing to preserve by
    /// letting it run.
    /// </summary>
    [Theory]
    [InlineData("ClosedUnmerged")]
    [InlineData("Absent")]
    public void A_parent_that_stops_being_open_takes_back_a_release_the_child_has_not_spent(string state)
    {
        TaskAggregate task = Assigned();
        task.Apply(Observed(RemoteParentState.Open, "feature/teammate-slice"));
        task.State.Should().Be(TaskState.Queued);

        task.Apply(Observed(state, headBranch: string.Empty));

        task.State.Should().Be(TaskState.Blocked,
            "an undispatched child is still the platform's to hold, and a fresh cut is still ahead of it");
        task.AwaitsRemoteStackedParent.Should().BeTrue();

        // Not a one-way trap: the same door that took the release back gives it again the moment
        // the pull request is open again, which is what a teammate reopening one produces.
        task.Apply(Observed(RemoteParentState.Open, "feature/teammate-slice"));
        task.State.Should().Be(TaskState.Queued);
    }

    /// <summary>
    /// The row the dispatcher's own Queued query reads, and the detail view beside it: the release
    /// has to come back on both, or the aggregate holds a child the board still offers up.
    /// </summary>
    [Fact]
    public void Both_projections_take_the_release_back_with_the_aggregate()
    {
        Guid taskId = DomainId.New();
        TaskDetailsProjection details = new();
        TaskListItemProjection rows = new();

        TaskDetails view = details.Create(new FakeEvent<TaskAdded>(Added(taskId)));
        TaskListItem row = rows.Create(new FakeEvent<TaskAdded>(Added(taskId)));

        details.Apply(new FakeEvent<TaskPublished>(new TaskPublished(taskId, Now, Owner)), view);
        rows.Apply(new FakeEvent<TaskPublished>(new TaskPublished(taskId, Now, Owner)), row);
        details.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(taskId, Owner, [], Now, Owner)), view);
        rows.Apply(new FakeEvent<TaskAssigned>(new TaskAssigned(taskId, Owner, [], Now, Owner)), row);
        view.State.Should().Be(TaskState.Blocked);
        row.State.Should().Be(TaskState.Blocked);

        RemoteStackedParentObserved open = Observed(RemoteParentState.Open, "feature/teammate-slice");
        details.Apply(new FakeEvent<RemoteStackedParentObserved>(open), view);
        rows.Apply(new FakeEvent<RemoteStackedParentObserved>(open), row);
        view.State.Should().Be(TaskState.Queued);
        row.State.Should().Be(TaskState.Queued);

        RemoteStackedParentObserved closed = Observed(RemoteParentState.ClosedUnmerged, string.Empty);
        details.Apply(new FakeEvent<RemoteStackedParentObserved>(closed), view);
        rows.Apply(new FakeEvent<RemoteStackedParentObserved>(closed), row);

        view.State.Should().Be(TaskState.Blocked, "the projection and the aggregate read one rule");
        row.State.Should().Be(TaskState.Blocked, "and this is the row the dispatcher asks for Queued work");
        row.BlockingHoldReason.Should().Contain("closed without merging",
            "which is what makes h9k status read the re-blocked child as NeedsHuman rather than as waiting");
    }

    /// <summary>
    /// A late observation of a pull request this task no longer declares must not land: a revision
    /// can repoint the edge between a sweep's read and its append, and letting it through would
    /// attribute one pull request's state to another — and, on an Open, dispatch the child.
    /// </summary>
    [Fact]
    public void An_observation_of_a_pull_request_the_task_no_longer_declares_is_dropped()
    {
        TaskAggregate task = Assigned();

        task.Apply(new RemoteStackedParentObserved(
            task.Id, ParentNumber + 1, RemoteParentState.Open, "feature/somebody-elses", "abc123",
            "main", "https://github.com/x/y/pull/265", null, "open", Now));

        task.State.Should().Be(TaskState.Blocked);
        task.RemoteStackedParentState.Should().Be(RemoteParentState.Unknown);
    }

    [Fact]
    public void Declaring_both_forms_at_once_is_refused()
    {
        Guid parentTaskId = DomainId.New();

        Action declare = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null,
            Now, Owner, blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId,
            stackedOnPullRequestNumber: ParentNumber);

        declare.Should().Throw<DomainValidationException>()
            .WithMessage("*stands on one parent*")
            .WithMessage("*--stacked-on-pull-request*");
    }

    /// <summary>
    /// Zero is refused with the negatives rather than read as "no edge declared" — that is int?'s
    /// own null, and dropping a declaration would dispatch the child onto the project's base with
    /// nobody told (Copilot review, pull request #277).
    /// </summary>
    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    public void A_pull_request_number_that_is_not_positive_is_refused(int declared)
    {
        Action declare = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null,
            Now, Owner, stackedOnPullRequestNumber: declared);

        declare.Should().Throw<DomainValidationException>().WithMessage("*not a pull request number*");
    }

    [Fact]
    public void A_pr_review_task_cannot_stand_on_a_pull_request_either()
    {
        Action declare = () => TaskDecider.VetStackedEdge(
            DomainId.New(), null, ParentNumber, [], TaskType.PrReview);

        declare.Should().Throw<DomainValidationException>()
            .WithMessage("*never opens one of its own*")
            .WithMessage("*--stacked-on-pull-request*");
    }

    /// <summary>
    /// Repointing a stack replaces whatever it stood on, across the two forms as well as within
    /// one — and the displaced form is recorded as cleared, or the stream would replay into a
    /// child holding two parents.
    /// </summary>
    [Fact]
    public void Declaring_a_remote_parent_displaces_a_local_one()
    {
        Guid parentTaskId = DomainId.New();
        Guid taskId = DomainId.New();
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null, Now, Owner,
            blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId));

        TaskRevised revised = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            Now, Owner, stackedOnPullRequestNumber: Optional<int?>.Of(ParentNumber));
        task.Apply(revised);

        revised.StackedOnTaskId.HasValue.Should().BeTrue("the displaced local edge is recorded as cleared");
        revised.StackedOnTaskId.Value.Should().BeNull();
        task.StackedOnTaskId.Should().BeNull();
        task.StackedOnPullRequestNumber.Should().Be(ParentNumber);
        task.BlockedBy.Should().Equal([parentTaskId],
            "dropping the stack leaves the blocked-by dependency itself untouched");
    }

    /// <summary>
    /// Repointing the remote edge discards what was observed about the parent it no longer names:
    /// those facts describe a different pull request, and carrying an Open forward would dispatch
    /// the child on its predecessor's state.
    /// </summary>
    [Fact]
    public void Repointing_the_remote_edge_forgets_the_previous_parents_observation()
    {
        TaskAggregate task = Assigned();
        task.Apply(Observed(RemoteParentState.Open, "feature/teammate-slice"));
        task.State.Should().Be(TaskState.Queued);

        task.Apply(new TaskRevised(
            task.Id, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            Now, Owner, StackedOnPullRequestNumber: Optional<int?>.Of(ParentNumber + 1)));

        task.RemoteStackedParentState.Should().Be(RemoteParentState.Unknown);
        task.RemoteStackedParentHeadBranch.Should().BeEmpty();
        task.AwaitsRemoteStackedParent.Should().BeTrue();
    }

    /// <summary>
    /// The base a fresh cut starts from is read entirely off the recorded observation — no provider
    /// call on the dispatch path — and every state but a live open pull request falls back to the
    /// project's own base with the reason recorded, the same one direction
    /// <see cref="StackedBaseResolver"/> is forgiving in for a local parent.
    /// </summary>
    [Fact]
    public void The_base_is_the_observed_head_branch_while_the_parent_is_open()
    {
        TaskDetails child = new()
        {
            Id = DomainId.New(),
            StackedOnPullRequestNumber = ParentNumber,
            RemoteStackedParentState = RemoteParentState.Open,
            RemoteStackedParentHeadBranch = "feature/teammate-slice",
        };

        StackedBase resolved = ResolveRemote(child);

        resolved.BaseBranch.Should().Be("feature/teammate-slice");
        resolved.ParentPullRequestNumber.Should().Be(ParentNumber);
        resolved.IsStacked.Should().BeTrue();
        resolved.Reason.Should().Contain($"#{ParentNumber}");
    }

    [Fact]
    public void A_merged_parent_leaves_the_child_on_the_projects_own_base()
    {
        TaskDetails child = new()
        {
            Id = DomainId.New(),
            StackedOnPullRequestNumber = ParentNumber,
            RemoteStackedParentState = RemoteParentState.Merged,
            RemoteStackedParentHeadBranch = "feature/teammate-slice",
        };

        StackedBase resolved = ResolveRemote(child);

        resolved.BaseBranch.Should().Be("main");
        resolved.IsStacked.Should().BeFalse(
            "there is nothing left to stack on, so no retarget and no replay are owed either");
        resolved.Reason.Should().Contain("has merged");
    }

    private static StackedBase ResolveRemote(TaskDetails child) =>
        StackedBaseResolver.ResolveRemote(
            child, new ProjectDetails { BaseBranch = "main" }, ParentNumber);

    /// <summary>
    /// The declaration as a bare event, for the two projections — which are fed events rather than
    /// a decider's output, and so cannot borrow <see cref="Declared"/>'s aggregate.
    /// </summary>
    private static TaskAdded Added(Guid taskId) =>
        new(taskId, DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null, Now, Owner,
            StartsAsDraft: true, StackedOnPullRequestNumber: ParentNumber);

    private static TaskAggregate Declared()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null,
            Now, Owner, stackedOnPullRequestNumber: ParentNumber));
        return task;
    }

    private static TaskAggregate Assigned()
    {
        TaskAggregate task = Declared();
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }

    private static RemoteStackedParentObserved Observed(RemoteParentState state, string headBranch) =>
        new(Guid.Empty, ParentNumber, state, headBranch, headBranch.Length == 0 ? string.Empty : "abc1234",
            "main", $"https://github.com/x/y/pull/{ParentNumber}", null, state.Describe(), Now);
}
