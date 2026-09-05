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

    /// <summary>
    /// The queue-first marker (task 45136b29, idea fcaded0b's R7 ruling) is the one revision
    /// Revise lets through past Draft — a Queued task, which nothing else about Revise's gate
    /// would ever admit.
    /// </summary>
    [Fact]
    public void Revise_of_a_queued_task_can_set_only_the_queue_first_marker()
    {
        TaskAggregate task = Queued();

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true)));

        task.QueuePriorityMarked.Should().BeTrue();
        task.State.Should().Be(TaskState.Queued, "the marker changes nothing else about the task");
    }

    /// <summary>
    /// The exception is narrow: a call that names the marker alongside anything else still needs
    /// the full unassign -> draft -> revise -> publish -> assign ceremony.
    /// </summary>
    [Fact]
    public void Revise_of_a_queued_task_refuses_when_queue_priority_travels_with_another_field()
    {
        TaskAggregate task = Queued();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.Of("Also reword it"), Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true));

        act.Should().Throw<DomainConflictException>().WithMessage("*only a draft can be revised*");
    }

    /// <summary>Abandoned is the one state Reopen never runs from, so marking it is refused rather than silently recorded.</summary>
    [Fact]
    public void Revise_of_an_abandoned_task_setting_only_queue_priority_refuses()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Abandon(task, "Stopped believing in it", Now, Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true));

        act.Should().Throw<DomainConflictException>().WithMessage("*nothing here will ever run again*a priority marker would do nothing*");
    }

    /// <summary>
    /// The interactive-mode-clearing mirror of the test above: Abandoned is also the one state
    /// nothing ever requeues from, so clearing a flag that only matters to a future run is
    /// refused the same way marking queue-first is (independent pre-PR review, cycle 1 of this
    /// task's own review-fix pass — the interactive-mode gap found in h9k task start).
    /// </summary>
    [Fact]
    public void Revise_of_an_abandoned_task_clearing_only_interactive_mode_refuses()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Abandon(task, "Stopped believing in it", Now, Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, clearInteractiveMode: true);

        act.Should().Throw<DomainConflictException>().WithMessage("*nothing here will ever run again*the interactive-mode flag would do nothing*");
    }

    /// <summary>
    /// Both marker-only revisions requested in the same call name both markers in the refusal
    /// rather than the queue-priority one alone (independent pre-PR review round 2, PR #224,
    /// Copilot): a message that only ever named the priority marker misdescribed the request the
    /// moment both were present at once.
    /// </summary>
    [Fact]
    public void Revise_of_an_abandoned_task_setting_both_markers_names_both_in_the_refusal()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Abandon(task, "Stopped believing in it", Now, Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true), clearInteractiveMode: true);

        act.Should().Throw<DomainConflictException>().WithMessage(
            "*nothing here will ever run again*a priority marker and the interactive-mode flag would both do nothing*");
    }

    /// <summary>
    /// Done is precisely and exclusively the state Reopen runs from (TaskDecider.Reopen), so the
    /// marker stays settable there too — for the follow-up run a later reopen might dispatch
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public void Revise_of_a_done_task_setting_only_queue_priority_succeeds()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/1", Now));

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true)));

        task.QueuePriorityMarked.Should().BeTrue();
        task.State.Should().Be(TaskState.Done, "the marker changes nothing else about the task");
    }

    /// <summary>
    /// The marker earns its clearing the moment a run actually dispatches for it, whichever kind
    /// of claim that turns out to be — it must not survive into the run it bought.
    /// </summary>
    [Fact]
    public void Claiming_a_marked_task_clears_the_queue_first_marker()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true)));
        task.QueuePriorityMarked.Should().BeTrue();

        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));

        task.QueuePriorityMarked.Should().BeFalse("the run it earned just dispatched");
    }

    /// <summary>
    /// A marker set on a currently-Claimed task (Decisions Log #127's own allowance) buys a
    /// future turn that never arrives if the run simply finishes — without this it would survive
    /// into Done and misreport a finished task as still waiting for one (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void Completing_a_marked_claim_clears_the_queue_first_marker()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true)));
        task.QueuePriorityMarked.Should().BeTrue();

        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/1", Now));

        task.QueuePriorityMarked.Should().BeFalse("the task reached Done without ever earning another claim");
    }

    /// <summary>Same reasoning as completing a marked claim, but for the walked-away exit.</summary>
    [Fact]
    public void Abandoning_a_marked_task_clears_the_queue_first_marker()
    {
        TaskAggregate task = Queued();
        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            epicId: Optional<Guid?>.None, queuePriority: Optional<bool>.Of(true)));
        task.QueuePriorityMarked.Should().BeTrue();

        task.Apply(TaskDecider.Abandon(task, "Stopped believing in it", Now, Owner));

        task.QueuePriorityMarked.Should().BeFalse();
    }

    /// <summary>
    /// A task joins an epic at creation, or later through the same revision gate every other
    /// field goes through — no separate ceremony (Decisions Log #100).
    /// </summary>
    [Fact]
    public void Add_can_join_a_task_to_an_epic_and_creation_without_one_is_unchanged()
    {
        Guid epicId = DomainId.New();
        TaskAdded joined = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "In an epic",
            acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, epicId: epicId);
        TaskAggregate withEpic = new();
        withEpic.Apply(joined);
        withEpic.EpicId.Should().Be(epicId);

        TaskAdded ungrouped = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Not in any epic",
            acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner);
        TaskAggregate withoutEpic = new();
        withoutEpic.Apply(ungrouped);
        withoutEpic.EpicId.Should().BeNull("membership is optional and never demanded at creation");
    }

    /// <summary>
    /// A revision that only touches the epic is a real revision — it does not have to also
    /// change something else to count as "something to revise" — and a task belongs to at most
    /// one epic at a time, so joining a second one replaces the first rather than adding to it.
    /// </summary>
    [Fact]
    public void A_task_joins_and_leaves_an_epic_through_revision()
    {
        TaskAggregate task = Draft();
        Guid firstEpic = DomainId.New();
        Guid secondEpic = DomainId.New();

        task.Apply(ReviseEpic(task, Optional<Guid?>.Of(firstEpic)));
        task.EpicId.Should().Be(firstEpic);

        task.Apply(ReviseEpic(task, Optional<Guid?>.Of(secondEpic)));
        task.EpicId.Should().Be(secondEpic, "a task belongs to at most one epic");

        task.Apply(ReviseEpic(task, Optional<Guid?>.Of(null)));
        task.EpicId.Should().BeNull("leaving is the same gate, with epicId cleared rather than set");
    }

    /// <summary>
    /// Task: the review pipeline's stage composition becomes configuration recorded per run —
    /// the task-level door, h9k task revise, is Draft-only and canonicalizes an alias into the
    /// value the resolver and h9k task show will read back.
    /// </summary>
    [Fact]
    public void A_revision_sets_a_review_stage_composition_override_and_default_clears_it()
    {
        TaskAggregate task = Draft();

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("adversarial-only"), reviewStageCompositionAcknowledged: true));

        task.ReviewStageComposition?.Value.Should().Be("AdversarialOnly", "the raw alias is canonicalized before it lands on the stream");

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("default")));

        task.ReviewStageComposition.Should().BeNull("'default' clears the task override so the project or node decides");
    }

    [Fact]
    public void A_revision_to_none_without_acknowledgment_is_refused_naming_the_consequence()
    {
        TaskAggregate task = Draft();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("none"));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*Decisions Log #92*")
            .WithMessage("*--accept-reduced-review*");
    }

    [Fact]
    public void A_revision_to_none_with_acknowledgment_records_the_attestation()
    {
        TaskAggregate task = Draft();

        TaskRevised revised = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("none"), reviewStageCompositionAcknowledged: true);

        revised.ReviewStageComposition.Value?.Value.Should().Be("None");
        revised.ReviewStageCompositionAcknowledged.Should().BeTrue();
    }

    /// <summary>
    /// Never assert an unobserved fact (AGENTS.md): a human passing --accept-reduced-review
    /// alongside a composition that never needed it must not have that recorded as though a real
    /// guarantee were traded away and accepted, the TaskPublished.UntrackedAttested clamp idiom.
    /// </summary>
    [Fact]
    public void A_revision_to_full_pipeline_never_records_an_attestation_even_if_acknowledgment_was_passed()
    {
        TaskAggregate task = Draft();

        TaskRevised revised = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("full-pipeline"), reviewStageCompositionAcknowledged: true);

        revised.ReviewStageComposition.Value?.Value.Should().Be("FullPipeline");
        revised.ReviewStageCompositionAcknowledged.Should().BeFalse(
            "full-pipeline never needed an acknowledgment, so recording one would assert a fact nobody observed");
    }

    [Fact]
    public void A_revision_to_an_unrecognized_composition_is_refused_with_the_recognized_values_quoted()
    {
        TaskAggregate task = Draft();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("bogus"));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*'bogus'*")
            .WithMessage("*full-pipeline*")
            .WithMessage("*conformance-only*");
    }

    [Fact]
    public void Revising_only_the_review_stage_composition_counts_as_something_to_revise()
    {
        TaskAggregate task = Draft();

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            reviewStageComposition: Optional<string?>.Of("conformance-only"), reviewStageCompositionAcknowledged: true));

        task.ReviewStageComposition?.Value.Should().Be("ConformanceOnly");
    }

    [Fact]
    public void Revising_an_ordinary_task_to_pr_review_is_refused_since_it_carries_no_pull_request()
    {
        TaskAggregate task = Draft();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.Of(TaskType.PrReview), Optional<AgentModel>.None,
            Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k task add --from-pr*", "the same door TaskAddCommand's own --from-pr guard names");
    }

    [Fact]
    public void Revising_a_pull_request_adopted_task_to_pr_review_is_allowed()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", acceptanceCriteria: [], TaskType.PrReview,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner));

        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.Of(TaskType.PrReview), Optional<AgentModel>.None,
            Now, Owner));

        task.Type.Should().Be(TaskType.PrReview);
    }

    /// <summary>
    /// The reverse of <see cref="Revising_an_ordinary_task_to_pr_review_is_refused_since_it_carries_no_pull_request"/>:
    /// a task adopted from a pull request holds its pr-review type just as firmly on the way out.
    /// Without this, a task adopted with h9k task add --from-pr could be revised to an ordinary
    /// build type and dispatched as ordinary work against a foreign pull request's title and
    /// body, while still carrying the pull-request ExternalReference the platform recorded it
    /// under.
    /// </summary>
    [Fact]
    public void Revising_a_pull_request_adopted_task_away_from_pr_review_is_refused()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", acceptanceCriteria: [], TaskType.PrReview,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.Of(TaskType.Feature), Optional<AgentModel>.None,
            Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*always a pr-review task*", "the task still carries the foreign pull request's reference");
    }

    /// <summary>
    /// A pr-review task's own pipeline is architecturally fixed — its primary session already is
    /// the adversarial lens, and PrReviewEngine dispatches the conformance lens second,
    /// unconditionally — so there is no point left where a review-stage-composition override could
    /// actually take effect. Recording one anyway used to let h9k task show's Stages column state a
    /// pipeline shape the run never honored (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void Add_refuses_a_review_stage_composition_override_on_a_pr_review_task()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", acceptanceCriteria: [], TaskType.PrReview,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner,
            reviewStageComposition: "none", reviewStageCompositionAcknowledged: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*pr-review task*")
            .WithMessage("*pipeline is fixed*");
    }

    [Fact]
    public void Revising_a_pr_review_tasks_review_stage_composition_is_refused()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", acceptanceCriteria: [], TaskType.PrReview,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            Now, Owner, reviewStageComposition: Optional<string?>.Of("adversarial-only"),
            reviewStageCompositionAcknowledged: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*pr-review task*")
            .WithMessage("*pipeline is fixed*");
    }

    /// <summary>
    /// The same refusal on the other door onto a task's type: revising an ordinary task to
    /// pr-review and setting a composition override in the same call must be caught too, not only
    /// a task that was already pr-review before the revise.
    /// </summary>
    [Fact]
    public void Revising_a_task_to_pr_review_while_also_setting_a_composition_is_refused()
    {
        // Added directly through the decider with a mismatched type/reference (the CLI's own
        // --from-pr guard would refuse this combination at creation) to reach the one precondition
        // Revise's own pr-review guard needs — a pull-request ExternalReference already on the
        // task — while still starting from an ordinary type, so this exercises the branch where
        // the effective type only becomes PrReview because of this very revise call.
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.Of(TaskType.PrReview), Optional<AgentModel>.None,
            Now, Owner, reviewStageComposition: Optional<string?>.Of("none"),
            reviewStageCompositionAcknowledged: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*pr-review task*")
            .WithMessage("*pipeline is fixed*");
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
        FluentActions.Invoking(() =>
                TaskDecider.DependencyFailed(task, dependencyId, "It failed and will not close out.", Now))
            .Should().Throw<DomainConflictException>("repeating one observation tells the human nothing new");

        // A death that changed shape is a different observation, not a repeat: the recorded
        // remedy must never outlive the state it was advice about (Decisions Log #61).
        task.Apply(TaskDecider.DependencyFailed(
            task, dependencyId, "It reads Done on a run that will never carry a merge.", Now.AddHours(1)));
        task.DependencyFailureReason.Should().Contain("never carry a merge");
        task.DeadDependencies.Should().HaveCount(1, "one blocker is one hold, however often it is restated");
    }

    [Fact]
    public void A_blocker_back_in_the_pipeline_lifts_the_hold_without_erasing_that_it_happened()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Failed, closedOut: false)], Now, Owner));
        TaskDependencyFailed died = TaskDecider.DependencyFailed(task, dependencyId, "It failed.", Now);
        task.Apply(died);

        // What the resolver appends one dispatch cycle after h9k task retry put the blocker back
        // to work: the blocker is Queued again, so the hold no longer describes anything.
        task.Apply(TaskDecider.DependencyRecovered(
            task, dependencyId, "It is Queued again.", Now.AddHours(1)));

        task.State.Should().Be(TaskState.Blocked, "the blocker still has to finish before this may run");
        task.DeadDependencies.Should().BeEmpty();
        task.DependencyFailureReason.Should().BeNull("h9k status must stop reading it as NeedsHuman");
        task.UnmetDependencies.Should().Equal([dependencyId]);
        died.Should().NotBeNull("the hold happened, and the record of it stays on the stream");
    }

    [Fact]
    public void A_retried_blocker_that_dies_again_is_held_again()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Failed, closedOut: false)], Now, Owner));
        task.Apply(TaskDecider.DependencyFailed(task, dependencyId, "It failed.", Now));
        task.Apply(TaskDecider.DependencyRecovered(
            task, dependencyId, "It is Queued again.", Now.AddHours(1)));

        task.Apply(TaskDecider.DependencyFailed(task, dependencyId, "It failed again.", Now.AddHours(2)));

        task.DeadDependencies.Should().Equal(dependencyId);
        task.DependencyFailureReason.Should().Be(
            "It failed again.", "hold, recover, hold — each one observed, never a one-shot flag");
    }

    [Fact]
    public void Recovering_one_of_two_dead_blockers_leaves_the_reason_describing_the_one_still_dead()
    {
        Guid stillDead = DomainId.New();
        Guid retried = DomainId.New();
        TaskAggregate task = Published(stillDead, retried);
        task.Apply(TaskDecider.Assign(
            task,
            Owner,
            [Dependency(stillDead, TaskState.Abandoned, closedOut: false), Dependency(retried, TaskState.Failed, closedOut: false)],
            Now,
            Owner));
        task.Apply(TaskDecider.DependencyFailed(task, stillDead, "The abandoned one.", Now));
        task.Apply(TaskDecider.DependencyFailed(task, retried, "The failed one.", Now));

        task.Apply(TaskDecider.DependencyRecovered(
            task, retried, "It is Queued again.", Now.AddHours(1)));

        task.DeadDependencies.Should().Equal(stillDead);
        task.DependencyFailureReason.Should().Be(
            "The abandoned one.", "the reason a human reads must name a blocker that is still dead");
    }

    [Fact]
    public void A_death_recorded_while_a_recovery_was_in_flight_keeps_holding_the_task()
    {
        // The race the recovery event cannot see: one pass reads the task, decides the retried
        // blocker is back, and commits — while another pass appends a second blocker's death in
        // between. Deriving what survives here, at apply time, is what makes the newer hold
        // stand; a reason snapshotted before that death would silence the hold for good, since
        // every later sweep finds that death already recorded and has nothing new to say
        // (review finding, 2026-08-21).
        Guid retried = DomainId.New();
        Guid diedMeanwhile = DomainId.New();
        TaskAggregate task = Published(retried, diedMeanwhile);
        task.Apply(TaskDecider.Assign(
            task,
            Owner,
            [Dependency(retried, TaskState.Failed, closedOut: false), Dependency(diedMeanwhile, TaskState.Queued, closedOut: false)],
            Now,
            Owner));
        task.Apply(TaskDecider.DependencyFailed(task, retried, "It failed.", Now));

        // Decided against the world the pass read, where only the retried blocker was dead.
        TaskDependencyRecovered inFlight = TaskDecider.DependencyRecovered(
            task, retried, "It is Queued again.", Now.AddHours(1));

        // The other pass gets its death in first.
        task.Apply(TaskDecider.DependencyFailed(
            task, diedMeanwhile, "The other one was abandoned.", Now.AddMinutes(30)));
        task.Apply(inFlight);

        task.DeadDependencies.Should().Equal(diedMeanwhile);
        task.DependencyFailureReason.Should().Be(
            "The other one was abandoned.", "the task is still held, and it must still say why");
    }

    [Fact]
    public void Completing_one_of_two_dead_blockers_leaves_the_reason_describing_the_one_still_dead()
    {
        Guid stillDead = DomainId.New();
        Guid retried = DomainId.New();
        TaskAggregate task = Published(stillDead, retried);
        task.Apply(TaskDecider.Assign(
            task,
            Owner,
            [Dependency(stillDead, TaskState.Abandoned, closedOut: false), Dependency(retried, TaskState.Failed, closedOut: false)],
            Now,
            Owner));
        task.Apply(TaskDecider.DependencyFailed(task, stillDead, "The abandoned one.", Now));
        task.Apply(TaskDecider.DependencyFailed(task, retried, "The failed one.", Now));

        // Retried, and this time it merged. Closeout carries no surviving reason the way a
        // recovery does, so the fallback is what the task still records about its other dead
        // blocker — the one the human actually has to act on.
        task.Apply(TaskDecider.DependencyCompleted(task, retried, Now.AddHours(1)));

        task.State.Should().Be(TaskState.Blocked, "the abandoned blocker is still unmet");
        task.DeadDependencies.Should().Equal(stillDead);
        task.DependencyFailureReason.Should().Be(
            "The abandoned one.", "a hold must never name a blocker that has since closed out");
    }

    [Fact]
    public void A_blocker_whose_death_changed_shape_becomes_the_newest_dead_one()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        TaskAggregate task = Published(first, second);
        task.Apply(TaskDecider.Assign(
            task,
            Owner,
            [Dependency(first, TaskState.Failed, closedOut: false), Dependency(second, TaskState.Abandoned, closedOut: false)],
            Now,
            Owner));
        task.Apply(TaskDecider.DependencyFailed(task, first, "It failed.", Now));
        task.Apply(TaskDecider.DependencyFailed(task, second, "It was abandoned.", Now.AddMinutes(1)));

        // The first blocker died a different death since, which is a fresh observation rather
        // than a restatement: it takes the newest slot, because DeadDependencies is read
        // backwards by everything that asks which reason still stands.
        task.Apply(TaskDecider.DependencyFailed(
            task, first, "It reads Done on a run that will never carry a merge.", Now.AddHours(1)));

        task.DeadDependencies.Should().Equal(second, first);
        task.DependencyFailureReason.Should().Contain("never carry a merge");

        task.Apply(TaskDecider.DependencyCompleted(task, second, Now.AddHours(2)));
        task.DependencyFailureReason.Should().Contain(
            "never carry a merge", "the newest death observed is the one the human is left reading");
    }

    [Fact]
    public void Revising_the_dead_blocker_out_of_the_dependency_set_clears_the_hold_too()
    {
        // The remedy the recorded reason teaches: unassign, draft, revise, publish, assign.
        // It predates the recovery event and has to keep working unchanged (Decisions Log #61).
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Abandoned, closedOut: false)], Now, Owner));
        task.Apply(TaskDecider.DependencyFailed(task, dependencyId, "It was abandoned.", Now));

        task.Apply(TaskDecider.Unassign(task, "Dropping the dead blocker", leaseHeld: false, Now, Owner));
        task.Apply(TaskDecider.ReturnToDraft(task, "Dropping the dead blocker", Now, Owner));
        task.Apply(TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.Of([]), Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));

        task.State.Should().Be(TaskState.Queued);
        task.DeadDependencies.Should().BeEmpty();
        task.DependencyFailureReason.Should().BeNull("the blocker it named is not a blocker any more");
    }

    [Fact]
    public void A_recovery_is_refused_for_a_blocker_no_hold_was_ever_recorded_against()
    {
        Guid dependencyId = DomainId.New();
        TaskAggregate task = Published(dependencyId);
        task.Apply(TaskDecider.Assign(
            task, Owner, [Dependency(dependencyId, TaskState.Queued, closedOut: false)], Now, Owner));

        FluentActions.Invoking(() => TaskDecider.DependencyRecovered(
                task, dependencyId, "It looks fine.", Now))
            .Should().Throw<DomainConflictException>("there is no hold to lift, and inventing one would be a guess");
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
        new(id, "A blocker", state, closedOut, CurrentRunState: null, PullRequestUrl: null, TaskType.Chore, []);

    private static TaskRevised Revise(TaskAggregate task, string objective) => TaskDecider.Revise(
        task, Optional<string>.Of(objective), Optional<IReadOnlyList<string>>.None, Optional<string>.None,
        Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner);

    private static TaskRevised ReviseEpic(TaskAggregate task, Optional<Guid?> epicId) => TaskDecider.Revise(
        task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
        Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner, epicId);

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
