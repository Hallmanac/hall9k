using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class TaskDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_without_objective_fails_the_readiness_contract()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: " ",
            acceptanceCriteria: ["it works"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*objective*");
    }

    /// <summary>
    /// The task override is the most specific link in the model chain (Decisions Log #33).
    /// It is vetted here because the value ends up on the executor's /bin/sh command line.
    /// </summary>
    [Fact]
    public void Add_canonicalizes_a_model_override_and_leaves_it_unknown_when_unstated()
    {
        TaskAdded withOverride = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Pin the model",
            acceptanceCriteria: ["it is recorded"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: " OPUS ");

        withOverride.Model.Should().Be(AgentModel.Opus);

        TaskAdded withoutOverride = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Let the chain decide",
            acceptanceCriteria: ["it is recorded"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        withoutOverride.Model.Should().Be(
            AgentModel.Unknown, "an unstated override defers to the role, project, and platform levels");
    }

    /// <summary>
    /// The word 'default' passes the shell charset check, so nothing but the value object
    /// stops it becoming a task override that spawns on the owner's personal setting.
    /// </summary>
    [Fact]
    public void Add_treats_a_model_of_default_as_no_override_rather_than_as_a_model_name()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Leave the model to the chain",
            acceptanceCriteria: ["It defers"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: " Default ");

        added.Model.Should().Be(
            AgentModel.Unknown, "'default' states no preference; it is never the model a session ran on");
    }

    [Fact]
    public void Add_rejects_a_model_that_could_not_be_handed_to_the_executors_shell()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Smuggle a command",
            acceptanceCriteria: ["it is refused"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New(), model: "opus; rm -rf /");

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }

    [Fact]
    public void OverrideReviewCaps_with_nothing_set_refuses()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*Nothing to change*");
    }

    [Fact]
    public void OverrideReviewCaps_accepts_zero_on_a_per_run_cap_as_the_takeover_lever()
    {
        TaskAggregate task = ClaimedTask();

        task.Apply(TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.Of(0), Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Now, Owner));

        task.MaxComplianceReviewCycles.Should().Be(0, "0 always parks immediately, since cycles-since-grant can never be negative");
    }

    [Fact]
    public void OverrideReviewCaps_rejects_a_negative_per_run_cap()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.Of(-1), Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*--max-compliance-review-cycles*");
    }

    [Fact]
    public void OverrideReviewCaps_rejects_a_lifetime_budget_below_one()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(0), Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*--lifetime-review-cycle-budget*");
    }

    /// <summary>
    /// Deliberately state-agnostic, unlike Revise (Decisions Log #34): the takeover lever needs
    /// to reach a task whose run is live right now, which is Claimed, not Draft. Each of the four
    /// caps is independent, so setting only one leaves the other three untouched.
    /// </summary>
    [Fact]
    public void OverrideReviewCaps_applies_on_a_claimed_task_and_leaves_the_other_three_caps_alone()
    {
        TaskAggregate task = ClaimedTask();

        task.Apply(TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.Of(1), Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Now, Owner));

        task.MaxComplianceReviewCycles.Should().Be(1, "the takeover lever: at or below the track's current cycle count");
        task.MaxAdversarialReviewCycles.Should().BeNull("absent means left alone");
        task.MaxFinalFullPassRounds.Should().BeNull("absent means left alone");
        task.LifetimeReviewCycleBudget.Should().BeNull("absent means left alone");
    }

    [Fact]
    public void OverrideReviewCaps_clears_an_override_with_a_present_null()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(40), Now, Owner));
        task.LifetimeReviewCycleBudget.Should().Be(40);

        task.Apply(TaskDecider.OverrideReviewCaps(
            task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(null), Now, Owner));

        task.LifetimeReviewCycleBudget.Should().BeNull("present-with-null clears the override back to the project or node");
    }

    /// <summary>
    /// Creation is identity, not readiness (Decisions Log #34): a draft exists in order to
    /// gather criteria, so demanding them at Add would put the gate in the wrong place. The
    /// gate is Publish, and the test below is the other half of this one.
    /// </summary>
    [Fact]
    public void Add_without_acceptance_criteria_produces_a_draft_rather_than_refusing()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Add rate limiting to auth endpoints",
            acceptanceCriteria: [" ", ""], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());

        added.AcceptanceCriteria.Should().BeEmpty("blank criteria are no criteria");
        added.StartsAsDraft.Should().BeTrue("every task h9k creates now starts as a draft");

        TaskAggregate task = new();
        task.Apply(added);
        task.State.Should().Be(TaskState.Draft);
    }

    [Fact]
    public void Publish_without_acceptance_criteria_fails_the_readiness_contract()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), objective: "Add rate limiting to auth endpoints",
            acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*acceptance criter*");
    }

    /// <summary>
    /// Backlog: publishing an untracked task under a tracking backlog policy. A project that
    /// tracks its backlog in GitHub issues is also a dedup gate: a draft with no linked item
    /// refuses to publish until a human or orchestrator either links what a search found, or
    /// attests none exists. The refusal states both resolutions verbatim.
    /// </summary>
    [Fact]
    public void Publish_under_a_github_issues_backlog_policy_refuses_an_unlinked_draft()
    {
        TaskAggregate task = DraftTask();

        Action act = () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.GitHubIssues);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*GitHub issues*")
            .WithMessage($"*h9k task link-issue {task.Id}*")
            .WithMessage($"*h9k task publish {task.Id} --no-existing-item*")
            .WithMessage($"*h9k task publish {task.Id} --untracked*");
    }

    [Fact]
    public void Publish_under_a_jira_backlog_policy_refuses_an_unlinked_draft()
    {
        TaskAggregate task = DraftTask();

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.Jira);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*Jira*")
            .WithMessage($"*h9k task link-jira {task.Id}*")
            .WithMessage($"*h9k task publish {task.Id} --no-existing-item*")
            .WithMessage($"*h9k task publish {task.Id} --untracked*");
    }

    /// <summary>
    /// Backlog: a task can be published deliberately untracked under a tracking backlog policy.
    /// The third way forward the refusal above names — --untracked skips creating or linking any
    /// external item, and records who chose it and when as the attestation on the stream.
    /// </summary>
    [Fact]
    public void Publish_records_the_untracked_attestation_and_proceeds()
    {
        TaskAggregate task = DraftTask();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.GitHubIssues,
            untracked: true);

        published.UntrackedAttested.Should().BeTrue();
        published.NoExistingItemAttested.Should().BeFalse();
        published.PublishedAt.Should().Be(Now, "the attestation's own when is the publish's when");
        published.PublishedByOwnerId.Should().Be(Owner, "the attestation's own who is the publisher");

        task.Apply(published);
        task.State.Should().Be(TaskState.Published);
    }

    [Fact]
    public void Publish_refuses_untracked_combined_with_no_existing_item_as_contradictory()
    {
        TaskAggregate task = DraftTask();

        Action act = () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.Jira,
            noExistingItemAttested: true, untracked: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--untracked*")
            .WithMessage("*--no-existing-item*");
    }

    [Fact]
    public void Publish_refuses_untracked_under_backlog_policy_none_as_meaningless()
    {
        TaskAggregate task = DraftTask();

        Action act = () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.None, untracked: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*policy none*")
            .WithMessage($"*h9k task publish {task.Id}*");
    }

    /// <summary>
    /// The same "reads as no tracking" convention <see cref="Publish_under_an_unrecognized_backlog_policy_never_asks_for_an_attestation"/>
    /// pins for a defensively-passed --no-existing-item must also apply to --untracked, which
    /// asserts a deliberate choice rather than clamping quietly — refusing it here is what keeps
    /// the two flags symmetric under both policy none and an unrecognized policy alike.
    /// </summary>
    [Fact]
    public void Publish_refuses_untracked_under_an_unrecognized_backlog_policy_as_meaningless_too()
    {
        TaskAggregate task = DraftTask();
        BacklogPolicy unrecognized = "SomeFutureTracker";

        Action act = () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, unrecognized, untracked: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage($"*h9k task publish {task.Id}*");
    }

    [Fact]
    public void Publish_never_records_an_untracked_attestation_the_gate_did_not_ask_for()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit"], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"),
            addedAt: Now, addedByOwnerId: Owner));

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.GitHubIssues, untracked: true);

        published.UntrackedAttested.Should().BeFalse(
            "a task that already carries a reference is never asked, so the flag is not recorded");
    }

    [Fact]
    public void Publish_records_the_no_existing_item_attestation_and_proceeds()
    {
        TaskAggregate task = DraftTask();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.GitHubIssues,
            noExistingItemAttested: true);

        published.NoExistingItemAttested.Should().BeTrue();
        published.PublishedAt.Should().Be(Now, "the attestation's own when is the publish's when");
        published.PublishedByOwnerId.Should().Be(Owner, "the attestation's own who is the publisher");

        task.Apply(published);
        task.State.Should().Be(TaskState.Published);
    }

    [Fact]
    public void Publish_under_backlog_policy_none_never_asks_for_an_attestation()
    {
        TaskAggregate task = DraftTask();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.None);

        published.NoExistingItemAttested.Should().BeFalse();
    }

    [Fact]
    public void Publish_of_an_already_linked_draft_needs_no_attestation()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit"], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"),
            addedAt: Now, addedByOwnerId: Owner));

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.GitHubIssues);

        published.NoExistingItemAttested.Should().BeFalse("a task that already carries a reference is never asked");
    }

    /// <summary>
    /// A draft with a publication already pending (h9k task push-to-jira, run while still a
    /// Draft) is already a session away from a card for this task — TrackInBacklogAsync already
    /// recognises and skips this exact state, so the gate must not refuse it too, and must not
    /// record an attestation it never asked for.
    /// </summary>
    [Fact]
    public void Publish_of_a_draft_with_a_pending_publication_needs_no_attestation()
    {
        TaskAggregate task = DraftTask();
        task.Apply(TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, Owner));

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.Jira);

        published.NoExistingItemAttested.Should().BeFalse(
            "a publication already pending is never asked, the same as an already-linked task");
    }

    /// <summary>
    /// A publication already pending still runs to completion regardless of what publish does
    /// here, so clamping --untracked the way an already-linked task's flag clamps would let that
    /// in-flight session defeat the operator's choice without a word. Refused instead, with the
    /// same "teach rather than swallow" reasoning as the policy-none refusal above.
    /// </summary>
    [Fact]
    public void Publish_refuses_untracked_on_a_draft_with_a_pending_publication()
    {
        TaskAggregate task = DraftTask();
        task.Apply(TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, Owner));

        Action act = () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.Jira, untracked: true);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*jira*")
            .WithMessage("*publication request outstanding*")
            .WithMessage($"*h9k task publish {task.Id}*");
    }

    [Fact]
    public void Publish_never_records_an_attestation_the_gate_did_not_ask_for()
    {
        TaskAggregate task = DraftTask();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, BacklogPolicy.None,
            noExistingItemAttested: true);

        published.NoExistingItemAttested.Should().BeFalse(
            "policy none never asked for the attestation, so a defensively-passed flag is not recorded");
    }

    /// <summary>
    /// The gate checks explicitly for Jira or GitHubIssues, the same convention
    /// TrackInBacklogAsync's own dispatch already uses, rather than "anything but None" — a
    /// persisted policy value the closed set no longer recognizes reads as "no tracking" there,
    /// and the dedup gate must read it identically rather than refuse to publish over it.
    /// </summary>
    [Fact]
    public void Publish_under_an_unrecognized_backlog_policy_never_asks_for_an_attestation()
    {
        TaskAggregate task = DraftTask();
        BacklogPolicy unrecognized = "SomeFutureTracker";

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, unrecognized);

        published.NoExistingItemAttested.Should().BeFalse(
            "an unrecognized policy is not Jira or GitHubIssues, so the gate never fires");
    }

    [Fact]
    public void Claim_increments_generation_and_carries_the_minted_run_id()
    {
        TaskAggregate task = QueuedTask();
        Guid runId = DomainId.New();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, runId, Now);

        claimed.LeaseGeneration.Should().Be(1);
        claimed.RunId.Should().Be(runId);

        task.Apply(claimed);
        task.State.Should().Be(TaskState.Claimed);
        task.CurrentRunId.Should().Be(runId);
        task.RunIds.Should().ContainSingle();
    }

    [Fact]
    public void Claim_of_a_claimed_task_conflicts()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Requeue_after_claim_returns_to_queued_and_a_reclaim_bumps_generation_again()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now));
        task.State.Should().Be(TaskState.Queued);

        TaskClaimed second = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        second.LeaseGeneration.Should().Be(2, "every claim increments the fencing token");
    }

    [Fact]
    public void ClaimInteractively_uses_the_empty_guid_sentinel_and_the_same_generation_fence_as_a_node_claim()
    {
        TaskAggregate task = QueuedTask();
        Guid runId = DomainId.New();

        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, Owner, runId, Now);

        claimed.NodeId.Should().Be(Guid.Empty, "h9k task work holds no node — a human, not a machine");
        claimed.LeaseGeneration.Should().Be(1);
        claimed.RunId.Should().Be(runId);

        task.Apply(claimed);
        task.State.Should().Be(TaskState.Claimed);
        task.IsInteractiveClaim.Should().BeTrue();
        task.ClaimedByNodeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ClaimInteractively_of_a_task_assigned_to_a_different_owner_conflicts()
    {
        TaskAggregate task = QueuedTask();

        Action act = () => TaskDecider.ClaimInteractively(task, DomainId.New(), DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>().WithMessage("*own owner*");
    }

    [Fact]
    public void ClaimInteractively_of_a_non_queued_task_conflicts()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.ClaimInteractively(task, Owner, DomainId.New(), Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void ReleaseInteractiveClaim_returns_to_queued_and_a_reclaim_bumps_generation_again()
    {
        TaskAggregate task = InteractivelyClaimedTask();

        task.Apply(TaskDecider.ReleaseInteractiveClaim(task, Now));

        task.State.Should().Be(TaskState.Queued);
        task.IsInteractiveClaim.Should().BeFalse();

        TaskClaimed reclaimed = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        reclaimed.LeaseGeneration.Should().Be(2, "every claim increments the fencing token, interactive or not");
    }

    [Fact]
    public void ReleaseInteractiveClaim_of_a_node_claimed_task_refuses()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.ReleaseInteractiveClaim(task, Now);

        act.Should().Throw<DomainConflictException>().WithMessage("*claimed by a node*");
    }

    [Fact]
    public void ReleaseInteractiveClaim_of_a_queued_task_refuses()
    {
        TaskAggregate task = QueuedTask();

        Action act = () => TaskDecider.ReleaseInteractiveClaim(task, Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void HandBack_carries_the_branch_forward_as_a_retry_branch_the_next_headless_claim_resumes()
    {
        TaskAggregate task = InteractivelyClaimedTask();
        Guid runId = task.CurrentRunId!.Value;

        TaskHandedBack handedBack = TaskDecider.HandBack(
            task, runId, "task/28b19893-add-rate-limiting", "Stepping away mid-migration.", Now, Owner);
        task.Apply(handedBack);

        task.State.Should().Be(TaskState.Queued);
        task.IsInteractiveClaim.Should().BeFalse();
        task.RetryBranch.Should().Be("task/28b19893-add-rate-limiting");
        task.CurrentRunId.Should().BeNull();
    }

    [Fact]
    public void HandBack_without_a_branch_fails_validation()
    {
        TaskAggregate task = InteractivelyClaimedTask();

        Action act = () => TaskDecider.HandBack(task, task.CurrentRunId!.Value, " ", null, Now, Owner);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void HandBack_of_a_node_claimed_task_refuses()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.HandBack(task, task.CurrentRunId!.Value, "task/x", null, Now, Owner);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Ask_then_answer_walks_needs_human_and_back()
    {
        TaskAggregate task = ClaimedTask();
        Guid questionId = DomainId.New();

        task.Apply(TaskDecider.Ask(task, questionId, task.CurrentRunId!.Value, "Which config wins?", Now));
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PendingQuestionId.Should().Be(questionId);

        task.Apply(TaskDecider.Answer(task, questionId, "The env var wins.", Now, DomainId.New()));
        task.State.Should().Be(TaskState.Claimed);
        task.PendingQuestionId.Should().BeNull();
    }

    [Fact]
    public void Answer_to_the_wrong_question_conflicts()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Ask(task, DomainId.New(), task.CurrentRunId!.Value, "A question", Now));

        Action act = () => TaskDecider.Answer(task, DomainId.New(), "answer", Now, DomainId.New());

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Complete_reaches_done_and_terminal_states_reject_further_decisions()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/1", Now));

        task.State.Should().Be(TaskState.Done);
        task.State.IsTerminal.Should().BeTrue();

        Action act = () => TaskDecider.Abandon(task, "changed my mind", Now, DomainId.New());
        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void Reopen_of_done_task_queues_a_follow_up_on_the_pull_request_branch()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        Guid previousRunId = task.CurrentRunId!.Value;

        TaskReopened reopened = TaskDecider.Reopen(
            task, previousRunId, "task/abc-branch", "Unresolved review comments",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());
        task.Apply(reopened);

        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be("task/abc-branch");
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7", "the follow-up updates the existing PR");
        task.ClaimedByNodeId.Should().BeNull("a reopened task is queued, and queued work is unclaimed");
        task.CurrentRunId.Should().BeNull();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        claimed.LeaseGeneration.Should().Be(2, "a follow-up claim moves the fencing token like any other");

        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, task.PullRequestUrl, Now));
        task.State.Should().Be(TaskState.Done);
        task.FollowUpBranch.Should().BeNull("completion consumes the follow-up marker");
    }

    [Fact]
    public void Automatic_reopens_count_toward_the_closeout_budget_and_a_manual_reopen_resets_it()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(1);
        task.FollowUpKind.Should().Be(FollowUpKind.FailingChecks, "the launcher picks the fix-the-CI prompt from it");

        CompleteFollowUp(task);
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown, "completion consumes the follow-up marker");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Unresolved Copilot threads.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(2, "the budget spans the whole closeout, not a single reopen");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Human asked for another attempt.",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(0, "a human-initiated reopen restores the automatic budget");
    }

    private static void CompleteFollowUp(TaskAggregate task)
    {
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, task.PullRequestUrl, Now));
    }

    /// <summary>
    /// Backlog 45: the progress cap counts consecutive laps against the SAME obstruction, and a
    /// lap that clears its obstruction — a different check now fails — restarts the count at
    /// the new obstruction's first lap, even though the lifetime ceiling (CloseoutAttempts)
    /// keeps climbing across both. A manual reopen wipes the obstruction slate exactly as it
    /// already wipes the lifetime counter.
    /// </summary>
    [Fact]
    public void The_progress_counter_tracks_the_obstruction_separately_from_the_lifetime_ceiling()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing: build.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New(),
            obstructionKey: "FailingChecks:build", obstructionSummary: "the failing check(s) build",
            knownHumanReviewThreadIds: [], knownPendingReviewRequestLogins: []));
        task.CloseoutAttempts.Should().Be(1);
        task.LastAutomaticObstructionKey.Should().Be("FailingChecks:build");
        task.ConsecutiveObstructionLaps.Should().Be(1);
        task.AutomaticLapHistory.Should().Equal("the failing check(s) build");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing: build.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New(),
            obstructionKey: "FailingChecks:build", obstructionSummary: "the failing check(s) build"));
        task.CloseoutAttempts.Should().Be(2, "the lifetime ceiling counts every automatic lap");
        task.ConsecutiveObstructionLaps.Should().Be(2, "the same obstruction repeated");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing: lint.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New(),
            obstructionKey: "FailingChecks:lint", obstructionSummary: "the failing check(s) lint"));
        task.CloseoutAttempts.Should().Be(3, "still the lifetime ceiling, unaffected by which obstruction");
        task.LastAutomaticObstructionKey.Should().Be("FailingChecks:lint");
        task.ConsecutiveObstructionLaps.Should().Be(
            1, "a different check is a different obstruction — its own first lap");
        task.AutomaticLapHistory.Should().Equal(
            "the failing check(s) build", "the failing check(s) build", "the failing check(s) lint");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Human asked for another attempt.",
            FollowUpKind.FailingChecks, automatic: false, Now, DomainId.New()));
        task.CloseoutAttempts.Should().Be(0);
        task.ConsecutiveObstructionLaps.Should().Be(0, "a manual reopen wipes the obstruction slate too");
        task.LastAutomaticObstructionKey.Should().BeNull();
        task.AutomaticLapHistory.Should().BeEmpty();
    }

    /// <summary>
    /// A `h9k task retry` that lands the work on a second pull request must not carry the
    /// first PR's closeout spend into the second — otherwise the second PR starts pre-debited
    /// and pre-capped, and a park message would misattribute the first PR's lap history to a
    /// pull request that no longer exists (independent pre-PR review, 2026-08-23).
    /// </summary>
    [Fact]
    public void A_retry_onto_a_new_pull_request_resets_the_closeout_counters()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing: build.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New(),
            obstructionKey: "FailingChecks:build", obstructionSummary: "the failing check(s) build",
            knownHumanReviewThreadIds: ["thread-1"], knownPendingReviewRequestLogins: ["teammate"]));
        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing: build.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New(),
            obstructionKey: "FailingChecks:build", obstructionSummary: "the failing check(s) build"));
        task.CloseoutAttempts.Should().Be(2);
        task.ConsecutiveObstructionLaps.Should().Be(2);

        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Fail(task, task.CurrentRunId!.Value, "Follow-up push rejected.", Now));
        task.Apply(TaskDecider.Retry(
            task, task.CurrentRunId, "task/abc-branch", "Rebuilding on a fresh PR.", Now, DomainId.New()));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/9", Now));

        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/9");
        task.CloseoutAttempts.Should().Be(0, "PR#9's closeout starts unencumbered by PR#7's spend");
        task.ConsecutiveObstructionLaps.Should().Be(0);
        task.LastAutomaticObstructionKey.Should().BeNull();
        task.AutomaticLapHistory.Should().BeEmpty();
        task.KnownHumanReviewThreadIds.Should().BeEmpty();
        task.KnownPendingReviewRequestLogins.Should().BeEmpty();
    }

    /// <summary>
    /// The human-engagement comparison points (unresolved human threads, pending review
    /// requests) travel forward on TaskReopened so the next automatic decision can tell a
    /// genuinely new one from something already accounted for (Decisions Log #80, backlog 45).
    /// </summary>
    [Fact]
    public void Known_human_engagement_sets_carry_forward_on_automatic_reopens_and_clear_on_manual_ones()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Unresolved review comments.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, DomainId.New(),
            obstructionKey: "ReviewFeedback:thread-1", obstructionSummary: "the same 1 unresolved review thread(s)",
            knownHumanReviewThreadIds: ["thread-1"],
            knownPendingReviewRequestLogins: ["teammate"]));

        task.KnownHumanReviewThreadIds.Should().Equal("thread-1");
        task.KnownPendingReviewRequestLogins.Should().Equal("teammate");

        CompleteFollowUp(task);
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Human asked for another attempt.",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New()));

        task.KnownHumanReviewThreadIds.Should().BeEmpty("a manual reopen wipes the comparison points too");
        task.KnownPendingReviewRequestLogins.Should().BeEmpty();
    }

    [Fact]
    public void Reopen_of_a_non_done_task_conflicts()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*only a done task*");
    }

    [Fact]
    public void Reopen_without_a_pull_request_conflicts()
    {
        TaskAggregate task = DoneTask(pullRequestUrl: null);

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*no pull request*");
    }

    /// <summary>
    /// A pr-review task's PullRequestUrl names the pull request it reviewed, not one this
    /// platform ever opened or pushed to (AGENTS.md: it "never writes to the pull request or the
    /// remote in any form"). Reopening it would resume a `pr/&lt;n&gt;` branch that never existed
    /// and eventually run the remote branch-delete cleanup against that foreign number once it
    /// merges — h9k pr resolve's ordinary reach never applies here.
    /// </summary>
    [Fact]
    public void Reopen_of_a_done_pr_review_task_conflicts()
    {
        TaskAggregate task = DonePrReviewTask();

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*no branch to resume*", "the task's PullRequestUrl names the PR it reviewed, not one it pushed to");
    }

    [Fact]
    public void Reopen_without_a_branch_fails_validation()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");

        Action act = () => TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, branch: " ", null,
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*branch*");
    }

    [Fact]
    public void Retry_of_a_failed_task_requeues_and_the_next_claim_moves_the_fencing_token()
    {
        TaskAggregate task = FailedTask();
        Guid failedRunId = task.CurrentRunId!.Value;

        TaskRetried retried = TaskDecider.Retry(
            task, failedRunId, "task/abc-branch", "Daemon push bug fixed; the work is intact.", Now, DomainId.New());
        task.Apply(retried);

        task.State.Should().Be(TaskState.Queued);
        task.RetryBranch.Should().Be("task/abc-branch", "the launcher resumes the failed run's branch when it survives");
        task.ClaimedByNodeId.Should().BeNull("a retried task is queued, and queued work is unclaimed");
        task.CurrentRunId.Should().BeNull();

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now);
        claimed.LeaseGeneration.Should().Be(2, "a retry claim moves the fencing token like any other");

        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/x/y/pull/9", Now));
        task.RetryBranch.Should().BeNull("completion consumes the retry marker");
    }

    [Fact]
    public void Retry_without_a_run_record_carries_no_branch_so_the_run_starts_clean()
    {
        TaskAggregate task = FailedTask();

        task.Apply(TaskDecider.Retry(task, previousRunId: null, branch: null, "Retrying anyway.", Now, DomainId.New()));

        task.State.Should().Be(TaskState.Queued);
        task.RetryBranch.Should().BeNull("no observed branch means a clean start from the base branch");
    }

    [Fact]
    public void Retry_of_a_non_failed_task_conflicts()
    {
        TaskAggregate queued = QueuedTask();
        Action retryQueued = () => TaskDecider.Retry(queued, null, null, "reason", Now, DomainId.New());
        retryQueued.Should().Throw<DomainConflictException>().WithMessage("*only a failed task*");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action retryAbandoned = () => TaskDecider.Retry(abandoned, null, null, "reason", Now, DomainId.New());
        retryAbandoned.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    [Fact]
    public void Retry_without_a_reason_fails_validation()
    {
        TaskAggregate task = FailedTask();

        Action act = () => TaskDecider.Retry(task, task.CurrentRunId, "task/abc", reason: " ", Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Failed_is_a_waypoint_and_only_done_and_abandoned_are_terminal()
    {
        FailedTask().State.IsTerminal.Should().BeFalse(
            "an unsolved problem is not an ending — Failed waits for a human (log #27)");
        TaskState.Done.IsTerminal.Should().BeTrue();
        TaskState.Abandoned.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Fail_of_an_already_failed_task_stays_rejected()
    {
        TaskAggregate task = FailedTask();

        TaskDecider.CanFail(task).Should().BeFalse("the daemon's pre-check mirrors the guard");
        Action act = () => TaskDecider.Fail(task, DomainId.New(), "another failure", Now);
        act.Should().Throw<DomainConflictException>().WithMessage("*already Failed*");
    }

    [Fact]
    public void Fail_of_a_done_or_abandoned_task_still_conflicts()
    {
        TaskAggregate done = DoneTask("https://github.com/x/y/pull/7");
        Action failDone = () => TaskDecider.Fail(done, DomainId.New(), "too late", Now);
        failDone.Should().Throw<DomainConflictException>().WithMessage("*already Done*");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action failAbandoned = () => TaskDecider.Fail(abandoned, DomainId.New(), "too late", Now);
        failAbandoned.Should().Throw<DomainConflictException>().WithMessage("*already Abandoned*");
    }

    [Fact]
    public void Resolve_of_a_failed_task_reaches_done_and_records_where_the_work_landed()
    {
        TaskAggregate task = FailedTask();

        TaskResolved resolved = TaskDecider.Resolve(
            task, "Work merged as PR #7; only the push step failed.",
            "https://github.com/x/y/pull/7", Now, DomainId.New());
        task.Apply(resolved);

        task.State.Should().Be(TaskState.Done);
        task.State.IsTerminal.Should().BeTrue("resolve is an explicit exit to an ending");
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7");
    }

    [Fact]
    public void Resolve_without_a_pull_request_keeps_the_one_already_on_the_stream()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Fail(task, task.CurrentRunId!.Value, "Follow-up push rejected.", Now));

        task.Apply(TaskDecider.Resolve(task, "The follow-up's work is on the PR already.", null, Now, DomainId.New()));

        task.State.Should().Be(TaskState.Done);
        task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7", "resolve never erases what was observed");
        task.FollowUpBranch.Should().BeNull("resolution consumes the pending follow-up marker");
    }

    [Fact]
    public void Resolved_task_with_a_pull_request_can_reopen_for_closeout_like_any_done_task()
    {
        TaskAggregate task = FailedTask();
        task.Apply(TaskDecider.Resolve(
            task, "Merged by hand as PR #7.", "https://github.com/x/y/pull/7", Now, DomainId.New()));

        TaskReopened reopened = TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "Unresolved review comments",
            FollowUpKind.ReviewFeedback, automatic: false, Now, DomainId.New());

        reopened.Should().NotBeNull("a resolved task is Done like any other, PR levers included");
    }

    [Fact]
    public void Resolve_of_a_non_failed_task_conflicts()
    {
        TaskAggregate queued = QueuedTask();
        Action resolveQueued = () => TaskDecider.Resolve(queued, "reason", null, Now, DomainId.New());
        resolveQueued.Should().Throw<DomainConflictException>().WithMessage("*only a failed task*");

        TaskAggregate done = DoneTask("https://github.com/x/y/pull/7");
        Action resolveDone = () => TaskDecider.Resolve(done, "reason", null, Now, DomainId.New());
        resolveDone.Should().Throw<DomainConflictException>("a done task has nothing to resolve");

        TaskAggregate abandoned = ClaimedTask();
        abandoned.Apply(TaskDecider.Abandon(abandoned, "not worth it", Now, DomainId.New()));
        Action resolveAbandoned = () => TaskDecider.Resolve(abandoned, "reason", null, Now, DomainId.New());
        resolveAbandoned.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    [Fact]
    public void Resolve_without_a_reason_fails_validation()
    {
        TaskAggregate task = FailedTask();

        Action act = () => TaskDecider.Resolve(task, reason: " ", null, Now, DomainId.New());

        act.Should().Throw<DomainValidationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Abandon_of_a_failed_task_is_the_walk_away_exit()
    {
        TaskAggregate task = FailedTask();

        task.Apply(TaskDecider.Abandon(task, "not worth another run", Now, DomainId.New()));

        task.State.Should().Be(TaskState.Abandoned,
            "walking away from a failure is the human ending Abandoned exists for (log #27)");
    }

    [Fact]
    public void Abandon_consumes_the_pending_work_markers()
    {
        TaskAggregate task = DoneTask("https://github.com/x/y/pull/7");
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc", "CI checks failing.",
            FollowUpKind.FailingChecks, automatic: true, Now, DomainId.New()));

        task.Apply(TaskDecider.Abandon(task, "not worth fixing", Now, DomainId.New()));

        task.FollowUpBranch.Should().BeNull("an abandoned task has no follow-up run pending");
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
    }

    [Fact]
    public void Abandon_after_a_retry_consumes_the_retry_marker()
    {
        TaskAggregate task = FailedTask();
        task.Apply(TaskDecider.Retry(
            task, task.CurrentRunId, "task/abc-branch", "One more attempt.", Now, DomainId.New()));

        task.Apply(TaskDecider.Abandon(task, "second thoughts", Now, DomainId.New()));

        task.RetryBranch.Should().BeNull("an abandoned task has no retry pending");
    }

    [Fact]
    public void Abandon_clears_the_pending_question_so_a_late_answer_cannot_resurrect_the_task()
    {
        TaskAggregate task = ClaimedTask();
        Guid questionId = DomainId.New();
        task.Apply(TaskDecider.Ask(task, questionId, task.CurrentRunId!.Value, "Which config wins?", Now));

        task.Apply(TaskDecider.Abandon(task, "no longer needed", Now, DomainId.New()));

        task.PendingQuestionId.Should().BeNull("Answer guards on the pending question, not on state");
        Action act = () => TaskDecider.Answer(task, questionId, "too late", Now, DomainId.New());
        act.Should().Throw<DomainConflictException>("Abandoned stays a dead end by design");
    }

    // Decisions Log #111, Brian's ruling 2026-08-30: h9k task set-session-cap is deliberately
    // state-agnostic, unlike Revise — it has to apply "even mid-run".

    [Fact]
    public void OverrideSessionCap_sets_the_cap_on_a_draft()
    {
        TaskAggregate task = DraftTask();

        TaskSessionCapOverridden overridden = TaskDecider.OverrideSessionCap(task, 1, Now, Owner);
        task.Apply(overridden);

        overridden.SessionCap.Should().Be(1);
        task.SessionCap.Should().Be(1);
    }

    [Fact]
    public void OverrideSessionCap_applies_while_the_tasks_run_is_live_unlike_revise()
    {
        TaskAggregate task = ClaimedTask();

        TaskSessionCapOverridden overridden = TaskDecider.OverrideSessionCap(task, 1, Now, Owner);
        task.Apply(overridden);

        task.SessionCap.Should().Be(1, "the cap can be set even while a run is live — a Draft-only "
            + "gate like Revise's would make h9k task set-session-cap useless for the case it exists for");
    }

    [Fact]
    public void OverrideSessionCap_refuses_a_cap_below_one()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.OverrideSessionCap(task, 0, Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*at least 1*", "a cap of zero would dispatch nothing for the run's next session");
    }

    /// <summary>
    /// The recovery <c>TaskDetails.SessionCap</c>'s own doc already promised ("null means the
    /// node's global default decides") but no command could reach until this fix: once pinned, the
    /// override used to be permanent for the task's whole life (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public void OverrideSessionCap_can_be_cleared_back_to_the_nodes_global_default()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.OverrideSessionCap(task, 1, Now, Owner));

        TaskSessionCapOverridden cleared = TaskDecider.OverrideSessionCap(task, null, Now, Owner);
        task.Apply(cleared);

        cleared.SessionCap.Should().BeNull();
        task.SessionCap.Should().BeNull("clearing the override returns the task to the node's global default");
    }

    [Fact]
    public void OverrideSessionCap_can_be_lowered_then_raised_and_the_latest_value_wins()
    {
        TaskAggregate task = ClaimedTask();

        task.Apply(TaskDecider.OverrideSessionCap(task, 1, Now, Owner));
        task.Apply(TaskDecider.OverrideSessionCap(task, 3, Now, Owner));

        task.SessionCap.Should().Be(3, "each override replaces the last, the same as a task's model override");
    }

    private static TaskAggregate FailedTask()
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Fail(task, task.CurrentRunId!.Value, "Push rejected: branch was rebased.", Now));
        return task;
    }

    private static TaskAggregate DoneTask(string? pullRequestUrl)
    {
        TaskAggregate task = ClaimedTask();
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, pullRequestUrl, Now));
        return task;
    }

    private static TaskAggregate DonePrReviewTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review the widgets PR", ["The findings report is accurate"],
            TaskType.PrReview, agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#7"),
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://github.com/acme/widgets/pull/7", Now));
        return task;
    }

    /// <summary>
    /// The owner these helpers assign to. Assignment is the dispatch trigger and the claim
    /// guard reads it (Decisions Log #34), so a task only reaches Queued through a named owner.
    /// </summary>
    private static readonly Guid Owner = DomainId.New();

    private static TaskAggregate DraftTask(params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit", "tests cover the limiter"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy));
        return task;
    }

    private static TaskAggregate PublishedTask(TaskDependencyGraph? graph = null)
    {
        TaskAggregate task = DraftTask();
        task.Apply(TaskDecider.Publish(task, graph ?? TaskDependencyGraph.Empty, Now, Owner));
        return task;
    }

    private static TaskAggregate QueuedTask()
    {
        TaskAggregate task = PublishedTask();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }

    private static TaskAggregate ClaimedTask()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        return task;
    }

    private static TaskAggregate InteractivelyClaimedTask()
    {
        TaskAggregate task = QueuedTask();
        task.Apply(TaskDecider.ClaimInteractively(task, Owner, DomainId.New(), Now));
        return task;
    }
}
