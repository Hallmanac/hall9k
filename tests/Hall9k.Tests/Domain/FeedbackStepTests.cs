using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The feedback step's record, with no database: the hold at each of the three doors a task is released through, what a held task
/// refuses, who may begin the step, what each end of it does to the task, the narrower override rule, and that the aggregate
/// and both projections read one answer.
/// </summary>
public sealed class FeedbackStepTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Node = DomainId.New();
    private static readonly Guid OtherNode = DomainId.New();
    private static readonly Guid BlockerId = DomainId.New();
    private const string Fingerprint = "sha256:owner-root";

    private static TaskDependency Blocker(bool closedOut) =>
        new(BlockerId, "Blocker", closedOut ? TaskState.Done : TaskState.Claimed, closedOut, null, null, TaskType.Feature, []);

    private static TaskAggregate Published(bool withBlocker = true, int? stackedOnPullRequest = null, bool? overrideTo = null)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Child", ["it works"], TaskType.Feature, "ctx", null, null, Now, Owner,
            blockedBy: withBlocker ? [BlockerId] : null, stackedOnPullRequestNumber: stackedOnPullRequest));
        if (overrideTo is { } value)
        {
            task.Apply(TaskDecider.Revise(
                task, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(value)));
        }

        task.Apply(TaskDecider.Publish(task, new TaskDependencyGraph([Blocker(true)]), Now, Owner));
        return task;
    }

    /// <summary>The task queued with every blocker already closed out, for a project whose setting is <paramref name="projectTakes"/>.</summary>
    private static TaskAggregate Held(bool projectTakes = true, Guid? placedOn = null)
    {
        TaskAggregate task = Published();
        task.Apply(TaskDecider.Assign(
            task, Owner, [Blocker(true)], Now, Owner, Fingerprint,
            placedOn is { } node ? Optional<Guid?>.Of(node) : default, projectTakes));
        return task;
    }

    [Fact]
    public void Queueing_a_task_that_takes_the_step_with_every_blocker_cleared_holds_it_instead_of_queueing_it()
    {
        TaskAggregate task = Held();

        task.State.Should().Be(TaskState.Blocked);
        task.FeedbackStepHeld.Should().BeTrue();
        task.UnmetDependencies.Should().BeEmpty("nothing is left to wait for, which is what makes it a hold");
    }

    [Fact]
    public void The_setting_is_off_by_default_and_a_task_that_does_not_take_the_step_is_queued_as_it_always_was()
    {
        TaskAggregate task = Held(projectTakes: false);

        task.State.Should().Be(TaskState.Queued);
        task.FeedbackStepHeld.Should().BeFalse();
        new ProjectDetails().TakesFeedbackStep.Should().BeFalse();
        new ProjectAggregate().TakesFeedbackStep.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void A_tasks_own_override_beats_the_projects_setting_either_way(bool projectTakes, bool overrideTo, bool expectedHeld)
    {
        TaskAggregate task = Published(overrideTo: overrideTo);
        task.Apply(TaskDecider.Assign(task, Owner, [Blocker(true)], Now, Owner, Fingerprint, default, projectTakes));

        task.FeedbackStepHeld.Should().Be(expectedHeld);
        task.State.Should().Be(expectedHeld ? TaskState.Blocked : TaskState.Queued);
    }

    [Fact]
    public void A_task_with_no_blockers_has_no_notes_to_read_and_is_never_held()
    {
        TaskAggregate task = Published(withBlocker: false);
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner, Fingerprint, default, projectTakesFeedbackStep: true));

        task.State.Should().Be(TaskState.Queued);
    }

    [Fact]
    public void The_last_blocker_clearing_holds_the_task_instead_of_releasing_it()
    {
        TaskAggregate task = Published();
        task.Apply(TaskDecider.Assign(task, Owner, [Blocker(false)], Now, Owner, Fingerprint, default, projectTakesFeedbackStep: true));
        task.State.Should().Be(TaskState.Blocked);
        task.FeedbackStepHeld.Should().BeFalse("a blocker is still open, so this is the ordinary wait");

        task.Apply(TaskDecider.DependencyCompleted(task, BlockerId, Now, projectTakesFeedbackStep: true));

        task.State.Should().Be(TaskState.Blocked);
        task.FeedbackStepHeld.Should().BeTrue();
        task.UnmetDependencies.Should().BeEmpty();
    }

    [Fact]
    public void The_observation_that_lets_a_stacked_child_go_is_a_release_point_too()
    {
        TaskAggregate task = Published(stackedOnPullRequest: 264);
        task.Apply(TaskDecider.Assign(task, Owner, [Blocker(true)], Now, Owner, Fingerprint, default, projectTakesFeedbackStep: true));
        task.State.Should().Be(TaskState.Blocked);
        task.FeedbackStepHeld.Should().BeFalse("the pull request has not been observed open yet, so the ordinary wait still stands");

        task.Apply(new RemoteStackedParentObserved(
            task.Id, 264, RemoteParentState.Open, "feature/parent", "abc1234", "main", "https://github.com/x/y/pull/264", null, "open", Now,
            HoldForFeedbackStep: FeedbackStepRules.Holds(task, projectTakesFeedbackStep: true)));

        task.State.Should().Be(TaskState.Blocked, "whichever of the three comes last is the release point");
        task.FeedbackStepHeld.Should().BeTrue();
    }

    [Fact]
    public void Once_the_step_has_begun_no_door_holds_the_task_again()
    {
        TaskAggregate task = Held();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        task.Apply(FeedbackStepDecider.Conclude(task, FeedbackStepOutcome.ReleasedAfterCheck, "fine", Now));
        task.State.Should().Be(TaskState.Queued);

        // A person dequeues and queues it again, and the project still takes the step: there is no second one.
        task.Apply(TaskDecider.Dequeue(task, null, leaseHeld: false, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [Blocker(true)], Now, Owner, Fingerprint, default, projectTakesFeedbackStep: true));

        task.State.Should().Be(TaskState.Queued);
        task.FeedbackStepHeld.Should().BeFalse();
        FeedbackStepRules.Holds(task, projectTakesFeedbackStep: true).Should().BeFalse();
    }

    [Fact]
    public void A_later_observation_of_the_pull_request_a_task_is_stacked_on_never_starts_a_second_step()
    {
        TaskAggregate task = Published(stackedOnPullRequest: 264);
        task.Apply(TaskDecider.Assign(task, Owner, [Blocker(true)], Now, Owner, Fingerprint, default, projectTakesFeedbackStep: true));
        task.Apply(Observed(task, RemoteParentState.Open));
        task.FeedbackStepHeld.Should().BeTrue();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        task.Apply(FeedbackStepDecider.Conclude(task, FeedbackStepOutcome.ReleasedAfterCheck, "fine", Now));
        task.State.Should().Be(TaskState.Queued);

        // The parent goes away and comes back: the task is re-blocked and released again, and the step does not run twice.
        task.Apply(Observed(task, RemoteParentState.ClosedUnmerged));
        task.State.Should().Be(TaskState.Blocked);
        task.Apply(Observed(task, RemoteParentState.Open));

        task.State.Should().Be(TaskState.Queued);
        task.FeedbackStepHeld.Should().BeFalse();
    }

    private static RemoteStackedParentObserved Observed(TaskAggregate task, RemoteParentState state) =>
        new(task.Id, 264, state, "feature/parent", "abc1234", "main", "https://github.com/x/y/pull/264", null, state.Describe(), Now,
            HoldForFeedbackStep: FeedbackStepRules.Holds(task, projectTakesFeedbackStep: true));

    [Fact]
    public void A_held_task_cannot_be_claimed_by_any_door_with_or_without_acknowledging_unmet_dependencies()
    {
        TaskAggregate task = Held();

        foreach (bool acknowledged in new[] { false, true })
        {
            Action interactively = () => TaskDecider.ClaimInteractively(
                task, Owner, DomainId.New(), Now, acknowledged, dependencyOverrideCarriedForward: acknowledged, ownerRootFingerprint: Fingerprint);
            Action deliberately = () => TaskDecider.ClaimDeliberately(
                task, Owner, DomainId.New(), Now, acknowledged, ownerRootFingerprint: Fingerprint);
            interactively.Should().Throw<DomainConflictException>().WithMessage("*held for its feedback step*");
            deliberately.Should().Throw<DomainConflictException>().WithMessage("*held for its feedback step*");
        }

        Action dispatcher = () => TaskDecider.Claim(task, Node, Owner, DomainId.New(), Now, Fingerprint);
        dispatcher.Should().Throw<DomainConflictException>().WithMessage("*held for its feedback step*");
    }

    [Fact]
    public void Dequeuing_a_held_task_ends_the_hold_and_leaves_the_task_published_with_its_assignee()
    {
        TaskAggregate task = Held(placedOn: Node);

        task.Apply(TaskDecider.Dequeue(task, "taking it out", leaseHeld: false, Now, Owner));

        task.State.Should().Be(TaskState.Published);
        task.FeedbackStepHeld.Should().BeFalse();
        task.AssigneeOwnerId.Should().Be(Owner);
        task.PlacedOnNodeId.Should().BeNull("the dequeue clears placement, which the requeue restores");
    }

    [Theory]
    [InlineData("ReleasedWithoutCheck")]
    [InlineData("ReleasedAfterCheck")]
    [InlineData("ReleasedAfterFailedCheck")]
    public void Each_of_the_three_released_outcomes_moves_the_held_task_to_Queued_in_the_same_event(string outcomeName)
    {
        TaskAggregate task = Held();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        task.FeedbackStepHeld.Should().BeTrue("beginning the step does not release the task");
        task.State.Should().Be(TaskState.Blocked);

        task.Apply(FeedbackStepDecider.Conclude(task, outcomeName, "reason", Now));

        task.State.Should().Be(TaskState.Queued);
        task.FeedbackStepHeld.Should().BeFalse();
        task.FeedbackStepResult!.Outcome.Value.Should().Be(outcomeName);
    }

    [Fact]
    public void Taking_the_task_back_dequeues_and_returns_it_to_draft_and_the_requeue_restores_owner_fingerprint_and_placement()
    {
        TaskAggregate task = Held(placedOn: Node);
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        Guid planId = DomainId.New();

        (TaskUnassigned dequeued, TaskReturnedToDraft drafted, FeedbackStepRefinementBegun begun) =
            FeedbackStepDecider.TakeBackToDraft(task, planId, Owner, Now);
        task.Apply(drafted);
        task.Apply(begun);

        dequeued.KeepsAssignee.Should().BeTrue();
        task.State.Should().Be(TaskState.Draft);
        task.AssigneeOwnerId.Should().Be(Owner);
        task.PlacedOnNodeId.Should().BeNull();
        task.FeedbackStepPlanId.Should().Be(planId);

        // The plan publishes it; the daemon then queues it again for what the task had before the step.
        task.Apply(TaskDecider.Publish(task, new TaskDependencyGraph([Blocker(true)]), Now, Owner));
        TaskAssigned requeued = FeedbackStepDecider.Requeue(task, [Blocker(true)], Now);
        task.Apply(requeued);

        requeued.AssignedOwnerId.Should().Be(Owner);
        requeued.AssignedOwnerRootFingerprint.Should().Be(Fingerprint);
        requeued.PlacedOnNodeId.Should().Be(Optional<Guid?>.Of(Node));
        requeued.HoldForFeedbackStep.Should().BeFalse();
        task.State.Should().Be(TaskState.Queued, "its blockers have cleared, so it reaches Queued at once");
        task.AssignedOwnerFingerprint.Should().Be(Fingerprint);
        task.AssigneeOwnerId.Should().Be(Owner);
        task.PlacedOnNodeId.Should().Be(Node);
    }

    [Fact]
    public void The_requeue_is_refused_when_the_assignee_changed_while_the_step_ran()
    {
        TaskAggregate task = Held();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        (TaskUnassigned dequeued, TaskReturnedToDraft drafted, FeedbackStepRefinementBegun begun) =
            FeedbackStepDecider.TakeBackToDraft(task, DomainId.New(), Owner, Now);
        task.Apply(drafted);
        task.Apply(begun);
        task.Apply(TaskDecider.Publish(task, new TaskDependencyGraph([Blocker(true)]), Now, Owner));
        task.Apply(new TaskAssigneeCleared(task.Id, null, Now, Owner));

        Action requeue = () => FeedbackStepDecider.Requeue(task, [Blocker(true)], Now);

        requeue.Should().Throw<DomainConflictException>().WithMessage("*assignee changed*");
        dequeued.Should().NotBeNull();
    }

    [Fact]
    public void Only_the_node_the_task_is_placed_on_may_begin_the_step_and_with_no_placement_only_a_node_of_the_queued_for_owner()
    {
        TaskAggregate placed = Held(placedOn: Node);
        FeedbackStepDecider.MayBeginOnThisNode(placed, Node, Owner, Fingerprint).Should().BeTrue();
        FeedbackStepDecider.MayBeginOnThisNode(placed, OtherNode, Owner, Fingerprint).Should().BeFalse("another node of the same owner is not the placed one");

        TaskAggregate unplaced = Held();
        FeedbackStepDecider.MayBeginOnThisNode(unplaced, OtherNode, Owner, Fingerprint).Should().BeTrue("with no placement any node of the owner may");
        FeedbackStepDecider.MayBeginOnThisNode(unplaced, Node, DomainId.New(), "sha256:a-teammate").Should().BeFalse("a teammate's node never begins it");

        unplaced.Apply(FeedbackStepDecider.Start(unplaced, Node, Now));
        FeedbackStepDecider.MayBeginOnThisNode(unplaced, Node, Owner, Fingerprint).Should().BeFalse("the step has begun, and begins once");
    }

    [Fact]
    public void A_step_that_has_begun_cannot_be_started_again()
    {
        TaskAggregate task = Held();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));

        Action again = () => FeedbackStepDecider.Start(task, OtherNode, Now);

        again.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void A_member_node_that_began_the_step_ends_it_on_every_peer_after_a_person_unassigns_or_reassigns_the_task()
    {
        EventReplicationInbox.SenderResolution member = new(Fingerprint, MembershipRole.Member, new HashSet<Guid> { Node });
        EventReplicationInbox.SenderResolution stranger = new("sha256:a-stranger", MembershipRole.Member, new HashSet<Guid> { OtherNode });

        TaskAggregate unassigned = Held();
        unassigned.Apply(FeedbackStepDecider.Start(unassigned, Node, Now));
        unassigned.Apply(TaskDecider.Dequeue(unassigned, null, false, Now, Owner));
        unassigned.Apply(new TaskAssigneeCleared(unassigned.Id, null, Now, Owner));
        unassigned.AssigneeOwnerFingerprint.Should().BeNull();

        TaskAggregate reassigned = Held();
        reassigned.Apply(FeedbackStepDecider.Start(reassigned, Node, Now));
        reassigned.Apply(TaskDecider.Dequeue(reassigned, null, false, Now, Owner));
        reassigned.Apply(new TaskAssigneeCleared(reassigned.Id, null, Now, Owner));
        reassigned.Apply(new TaskAssigneeSet(reassigned.Id, DomainId.New(), "sha256:a-teammate", Now, Owner));
        reassigned.AssigneeOwnerFingerprint.Should().Be("sha256:a-teammate");

        foreach (TaskAggregate task in new[] { unassigned, reassigned })
        {
            FeedbackStepOutcomeRecorded ended = FeedbackStepDecider.Conclude(
                task, FeedbackStepOutcome.EndedWithoutActing, "a person unassigned or reassigned the task while the step ran", Now);

            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, typeof(FeedbackStepOutcomeRecorded), ended, task, member, Node, Node)
                .Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, "the node that began the step ends it");
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, typeof(FeedbackStepOutcomeRecorded), ended, task, stranger, OtherNode, OtherNode)
                .Should().NotBe(EventReplicationInbox.TaskActVerdict.Allowed, "a sender with no part in the step is judged by the general rule");
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, typeof(FeedbackStepOutcomeRecorded), ended, task, member, OtherNode, OtherNode)
                .Should().NotBe(EventReplicationInbox.TaskActVerdict.Allowed, "another node of the same root did not begin it");
        }
    }

    [Fact]
    public void The_check_is_dispatched_once_and_its_outcome_recorded_against_the_session()
    {
        TaskAggregate task = Held();
        task.Apply(FeedbackStepDecider.Start(task, Node, Now));
        Guid session = DomainId.New();
        task.Apply(FeedbackStepDecider.DispatchCheck(task, session, Node, AgentModel.FromInput("claude-sonnet-5-5"), AgentEffort.Medium, Now));
        task.RunningFeedbackCheckSessionId.Should().Be(session);

        Action second = () => FeedbackStepDecider.DispatchCheck(task, DomainId.New(), Node, AgentModel.Unknown, AgentEffort.Medium, Now);
        second.Should().Throw<DomainConflictException>().WithMessage("*checks once*");

        task.Apply(FeedbackStepDecider.RecordCheck(task, session, Node, FeedbackCheckAnswer.Failed, RefinementFailureReason.TimedOut, Now));
        task.RunningFeedbackCheckSessionId.Should().BeNull();
        task.LatestFeedbackCheck!.Failure.Should().Be(RefinementFailureReason.TimedOut);
    }

    [Fact]
    public void The_override_is_refused_once_the_task_is_held_for_its_feedback_step_with_the_reason()
    {
        TaskAggregate task = Held();

        Action revise = () => TaskDecider.Revise(
            task, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(false));

        revise.Should().Throw<DomainConflictException>().WithMessage("*held for its feedback step*");
    }

    [Fact]
    public void The_override_is_refused_once_the_task_has_been_released_to_Queued_with_the_reason()
    {
        TaskAggregate task = Held(projectTakes: false);
        task.State.Should().Be(TaskState.Queued);

        Action revise = () => TaskDecider.Revise(
            task, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(true));

        revise.Should().Throw<DomainConflictException>().WithMessage("*released to Queued*");
    }

    [Fact]
    public void The_override_is_refused_on_a_claimed_task_with_the_reason()
    {
        TaskAggregate task = Held(projectTakes: false);
        task.Apply(TaskDecider.Claim(task, Node, Owner, DomainId.New(), Now, Fingerprint));

        Action revise = () => TaskDecider.Revise(
            task, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(true));

        revise.Should().Throw<DomainConflictException>().WithMessage("*has been claimed*");
    }

    [Fact]
    public void The_override_is_allowed_while_the_task_is_a_draft_published_or_blocked_and_not_held_and_alone_in_the_call()
    {
        TaskAggregate draft = new();
        draft.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Child", ["it works"], TaskType.Feature, "ctx", null, null, Now, Owner, blockedBy: [BlockerId]));
        TaskDecider.Revise(
            draft, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(true))
            .FeedbackStep.Value.Should().BeTrue();

        TaskAggregate published = Published();
        TaskRevised revised = TaskDecider.Revise(
            published, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(false));
        published.Apply(revised);
        published.FeedbackStepOverride.Should().BeFalse();

        TaskAggregate blocked = Published();
        blocked.Apply(TaskDecider.Assign(blocked, Owner, [Blocker(false)], Now, Owner, Fingerprint));
        blocked.State.Should().Be(TaskState.Blocked);
        TaskDecider.Revise(
            blocked, default, default, default, default, default, default, Now, Owner, feedbackStep: Optional<bool?>.Of(null))
            .FeedbackStep.HasValue.Should().BeTrue("'default' clears the override, present with null");

        Action withOtherChanges = () => TaskDecider.Revise(
            published, Optional<string>.Of("A different objective"), default, default, default, default, default, Now, Owner,
            feedbackStep: Optional<bool?>.Of(true));
        withOtherChanges.Should().Throw<DomainConflictException>("a revision of the contract still needs the Draft ceremony");
    }

    [Fact]
    public void The_project_setting_is_a_team_field_that_the_aggregate_and_the_projection_read_alike()
    {
        ProjectAggregate project = new();
        Guid projectId = DomainId.New();
        project.Apply(ProjectDecider.Register(projectId, Owner, DomainId.New(), "demo", "/tmp/demo", null, "main", Now));
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project, default, default, default, Now, Owner, takesFeedbackStep: Optional<bool>.Of(true));

        ProjectTeamSettingsChanged? team = ProjectTeamSettingsChanged.From(changed);

        team.Should().NotBeNull("the setting travels to every teammate's node");
        team!.TakesFeedbackStep.Should().Be(Optional<bool>.Of(true));
        project.Apply(changed);
        project.TakesFeedbackStep.Should().BeTrue();

        ProjectDetails details = new();
        new ProjectDetailsProjection().Apply(new FakeEvent<ProjectTeamSettingsChanged>(team), details);
        details.TakesFeedbackStep.Should().BeTrue();
    }

    [Fact]
    public void The_aggregate_and_both_projections_replay_the_hold_and_its_release_to_one_answer()
    {
        Guid taskId = DomainId.New();
        TaskDetailsProjection details = new();
        TaskListItemProjection rows = new();
        TaskAdded added = new(
            taskId, DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null, Now, Owner,
            BlockedBy: [BlockerId], StartsAsDraft: true);
        TaskDetails view = details.Create(new FakeEvent<TaskAdded>(added));
        TaskListItem row = rows.Create(new FakeEvent<TaskAdded>(added));
        TaskAssigned assigned = new(taskId, Owner, [], Now, Owner, Fingerprint, default, HoldForFeedbackStep: true);
        details.Apply(new FakeEvent<TaskPublished>(new TaskPublished(taskId, Now, Owner)), view);
        rows.Apply(new FakeEvent<TaskPublished>(new TaskPublished(taskId, Now, Owner)), row);

        details.Apply(new FakeEvent<TaskAssigned>(assigned), view);
        rows.Apply(new FakeEvent<TaskAssigned>(assigned), row);

        view.State.Should().Be(TaskState.Blocked);
        row.State.Should().Be(TaskState.Blocked);
        (view.FeedbackStepHeld, row.FeedbackStepHeld).Should().Be((true, true));

        FeedbackStepStarted started = new(taskId, Node, Now, Owner, Fingerprint, null, [BlockerId]);
        details.Apply(new FakeEvent<FeedbackStepStarted>(started), view);
        rows.Apply(new FakeEvent<FeedbackStepStarted>(started), row);
        view.FeedbackStepRunning.Should().BeTrue();

        FeedbackStepOutcomeRecorded outcome = new(taskId, FeedbackStepOutcome.ReleasedWithoutCheck, "no note", Now);
        details.Apply(new FakeEvent<FeedbackStepOutcomeRecorded>(outcome), view);
        rows.Apply(new FakeEvent<FeedbackStepOutcomeRecorded>(outcome), row);

        view.State.Should().Be(TaskState.Queued);
        row.State.Should().Be(TaskState.Queued);
        (view.FeedbackStepHeld, row.FeedbackStepHeld, view.FeedbackStepRunning).Should().Be((false, false, false));
        row.FeedbackStepOutcome.Should().Be(FeedbackStepOutcome.ReleasedWithoutCheck);
    }

    [Fact]
    public void A_late_completion_naming_a_sibling_that_already_finished_does_not_put_it_back_on_a_held_view()
    {
        Guid taskId = DomainId.New();
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        TaskDetailsProjection details = new();
        TaskListItemProjection rows = new();
        TaskAdded added = new(
            taskId, DomainId.New(), "Child", ["it works"], TaskType.Feature, null, null, null, Now, Owner,
            BlockedBy: [first, second], StartsAsDraft: true);
        TaskDetails view = details.Create(new FakeEvent<TaskAdded>(added));
        TaskListItem row = rows.Create(new FakeEvent<TaskAdded>(added));
        TaskPublished published = new(taskId, Now, Owner);
        details.Apply(new FakeEvent<TaskPublished>(published), view);
        rows.Apply(new FakeEvent<TaskPublished>(published), row);
        TaskAssigned assigned = new(taskId, Owner, [first, second], Now, Owner, Fingerprint);
        details.Apply(new FakeEvent<TaskAssigned>(assigned), view);
        rows.Apply(new FakeEvent<TaskAssigned>(assigned), row);
        TaskDependencyCompleted secondDone = new(taskId, second, [first], Now);
        TaskDependencyCompleted firstDone = new(taskId, first, [], Now, HoldForFeedbackStep: true);
        TaskDependencyCompleted late = new(taskId, first, [second], Now);
        foreach (TaskDependencyCompleted completed in new[] { secondDone, firstDone })
        {
            details.Apply(new FakeEvent<TaskDependencyCompleted>(completed), view);
            rows.Apply(new FakeEvent<TaskDependencyCompleted>(completed), row);
        }

        details.Apply(new FakeEvent<TaskDependencyCompleted>(late), view);
        rows.Apply(new FakeEvent<TaskDependencyCompleted>(late), row);

        view.FeedbackStepHeld.Should().BeTrue();
        view.UnmetDependencies.Should().BeEmpty("the late event's snapshot is older than the completion that already removed the sibling");
        row.UnmetDependencies.Should().BeEmpty();
    }

    [Fact]
    public void The_closed_vocabularies_round_trip_and_read_a_stranger_as_unknown()
    {
        ((FeedbackStepOutcome)"Parked").Should().Be(FeedbackStepOutcome.Parked);
        ((FeedbackStepOutcome)"nonsense").Should().Be(FeedbackStepOutcome.Unknown);
        FeedbackCheckAnswer.TryParseWord(" Affects ", out FeedbackCheckAnswer affects).Should().BeTrue();
        affects.Should().Be(FeedbackCheckAnswer.Affects);
        FeedbackCheckAnswer.TryParseWord("maybe", out _).Should().BeFalse();
    }
}
