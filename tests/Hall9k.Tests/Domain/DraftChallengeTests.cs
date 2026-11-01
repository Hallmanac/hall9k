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
/// The decider and aggregate side of a draft's challenge: who may be asked for one, what the publish gate reads, and what
/// it records. Pure, so no database: aggregates move only through Apply, as the standards ask.
/// </summary>
public sealed class DraftChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Node = DomainId.New();

    private static TaskAggregate Draft(TaskType? type = null, params string[] criteria)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Test the draft", criteria.Length == 0 ? ["It is tested"] : criteria,
            type ?? TaskType.Feature, agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner));
        return task;
    }

    private static DraftChallengeRecorded Verdict(
        TaskAggregate task, ChallengeVerdict verdict, int must = 0, int should = 0, int park = 0) =>
        new(task.Id, DomainId.New(), Node, verdict, ChallengeFailureReason.Unknown, must, should, park,
            AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private static DraftChallengeRecorded Failure(TaskAggregate task, ChallengeFailureReason reason) =>
        new(task.Id, null, Node, ChallengeVerdict.Unknown, reason, 0, 0, 0, AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private static void Revise(TaskAggregate task) =>
        task.Apply(TaskDecider.Revise(
            task, Optional<string>.Of("Test the draft, revised"), default, default, default, default, default, Now, Owner));

    private static Action Publish(TaskAggregate task, bool required = true, string? skip = null) =>
        () => TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, challenge: new ChallengeGateInput(required, "demo", skip));

    // ---- the request ----

    [Fact]
    public void A_draft_with_criteria_can_be_challenged_and_the_request_marks_it_without_naming_a_node()
    {
        TaskAggregate task = Draft();

        DraftChallengeRequested requested = TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null);
        task.Apply(requested);

        task.ChallengeRequested.Should().BeTrue();
        typeof(DraftChallengeRequested).GetProperties().Select(property => property.Name)
            .Should().NotContain("NodeId", "the request never leaves its node, so it names none");
    }

    [Fact]
    public void A_task_that_is_not_a_draft_is_refused_with_the_reason()
    {
        TaskAggregate task = Draft();
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));

        Action act = () => TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null);

        act.Should().Throw<DomainConflictException>().WithMessage("*not Draft*");
    }

    [Fact]
    public void A_draft_without_criteria_an_archived_project_and_a_missing_checkout_are_each_refused_by_name()
    {
        TaskAggregate noCriteria = new();
        noCriteria.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "No criteria yet", [], TaskType.Feature, null, null, null, Now, Owner));
        Action withoutCriteria = () => TaskDecider.RequestChallenge(noCriteria, Now, Owner, false, "demo", null);
        withoutCriteria.Should().Throw<DomainValidationException>().WithMessage("*acceptance criteria*");

        Action archived = () => TaskDecider.RequestChallenge(Draft(), Now, Owner, true, "demo", null);
        archived.Should().Throw<DomainConflictException>().WithMessage("*archived*");

        Action noCheckout = () => TaskDecider.RequestChallenge(Draft(), Now, Owner, false, "demo", "/nowhere/dev");
        noCheckout.Should().Throw<DomainBusinessRuleException>().WithMessage("*no working checkout*/nowhere/dev*");
    }

    [Fact]
    public void A_challenge_already_requested_or_running_refuses_another_and_a_recorded_one_does_not()
    {
        TaskAggregate task = Draft();
        task.Apply(TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null));
        Action whileRequested = () => TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null);
        whileRequested.Should().Throw<DomainConflictException>().WithMessage("*already has a challenge requested*");

        Guid session = DomainId.New();
        task.Apply(new DraftChallengeDispatched(task.Id, session, Node, Now, AgentModel.ChallengeDefault, AgentEffort.High));
        task.ChallengeRequested.Should().BeFalse();
        Action whileRunning = () => TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null);
        whileRunning.Should().Throw<DomainConflictException>().WithMessage("*already has a challenge running*");

        task.Apply(Failure(task, ChallengeFailureReason.TimedOut));
        TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null).Should().NotBeNull(
            "a recorded failure may be challenged again by repeating the verb");
    }

    // ---- the state a challenge leaves ----

    [Fact]
    public void A_newer_verdict_replaces_an_older_one_and_a_failure_replaces_nothing()
    {
        TaskAggregate task = Draft();
        DraftChallengeRecorded first = Verdict(task, ChallengeVerdict.DoNotPublish, must: 1);
        task.Apply(first);
        task.LatestChallengeVerdict.Should().Be(first);

        DraftChallengeRecorded failure = Failure(task, ChallengeFailureReason.NoReport);
        task.Apply(failure);
        task.LatestChallengeOutcome.Should().Be(failure);
        task.LatestChallengeVerdict.Should().Be(first, "a failure replaces no verdict");

        DraftChallengeRecorded second = Verdict(task, ChallengeVerdict.PublishAsWritten);
        task.Apply(second);
        task.LatestChallengeVerdict.Should().Be(second);
    }

    private static TaskRevised EffortOnlyRevision(TaskAggregate task) =>
        new(task.Id, default, default, default, default, default, default, Now, Owner, Effort: Optional<AgentEffort>.Of(AgentEffort.High));

    [Fact]
    public void A_revision_that_leaves_the_draft_text_alone_is_not_a_revision_since_the_verdict()
    {
        TaskAggregate task = Draft();
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));

        task.Apply(EffortOnlyRevision(task));

        task.RevisedSinceChallengeVerdict.Should().BeFalse("effort is not the objective, a criterion, or the context");
        Publish(task).Should().Throw<DomainException>("the must is still unapplied");
    }

    [Fact]
    public void A_revision_while_the_session_ran_ages_its_verdict_but_does_not_apply_the_verdicts_must()
    {
        TaskAggregate task = Draft();
        task.Apply(new DraftChallengeDispatched(task.Id, DomainId.New(), Node, Now, AgentModel.ChallengeDefault, AgentEffort.High));
        Revise(task);
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));

        task.LatestChallengeVerdictJudgedOlderDraft.Should().BeTrue("the session judged the text as it was at dispatch");
        task.RevisedSinceChallengeVerdict.Should().BeFalse("that revision was made before the verdict existed, so it cannot have applied the must");
        Publish(task).Should().Throw<DomainException>("the must is still unapplied");

        Revise(task);
        task.RevisedSinceChallengeVerdict.Should().BeTrue();
        Publish(task).Should().NotThrow("a revision after the verdict landed can have applied the must");

        task.Apply(new DraftChallengeDispatched(task.Id, DomainId.New(), Node, Now, AgentModel.ChallengeDefault, AgentEffort.High));
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));
        task.LatestChallengeVerdictJudgedOlderDraft.Should().BeFalse("the next session started after the revision and read the revised text");
        task.RevisedSinceChallengeVerdict.Should().BeFalse();
    }

    [Fact]
    public void A_revision_of_a_recorded_fact_the_prompt_carries_counts_as_applying_a_must()
    {
        TaskAggregate task = Draft();
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));

        task.Apply(new TaskRevised(
            task.Id, default, default, default, Optional<IReadOnlyList<Guid>>.Of([DomainId.New()]), default, default, Now, Owner));

        task.RevisedSinceChallengeVerdict.Should().BeTrue("a must can be about a dependency, and revising one applies it");
    }

    [Fact]
    public void A_text_free_revision_while_the_session_ran_does_not_age_its_verdict()
    {
        TaskAggregate task = Draft();
        task.Apply(new DraftChallengeDispatched(task.Id, DomainId.New(), Node, Now, AgentModel.ChallengeDefault, AgentEffort.High));
        task.Apply(EffortOnlyRevision(task));
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));

        task.LatestChallengeVerdictJudgedOlderDraft.Should().BeFalse();
    }

    [Fact]
    public void A_revision_after_a_verdict_is_remembered_and_a_new_verdict_clears_it()
    {
        TaskAggregate task = Draft();
        Revise(task);
        task.RevisedSinceChallengeVerdict.Should().BeFalse("there was no verdict to be revised since");

        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));
        task.RevisedSinceChallengeVerdict.Should().BeFalse();
        Revise(task);
        task.RevisedSinceChallengeVerdict.Should().BeTrue();

        task.Apply(Verdict(task, ChallengeVerdict.PublishAsRevised, must: 1));
        task.RevisedSinceChallengeVerdict.Should().BeFalse();
    }

    [Fact]
    public void Recording_a_challenge_changes_nothing_about_the_draft()
    {
        TaskAggregate task = Draft();
        (string objective, string[] criteria, string? context, TaskType type, TaskState state) =
            (task.Objective, [.. task.AcceptanceCriteria], task.AgentContext, task.Type, task.State);

        task.Apply(TaskDecider.RequestChallenge(task, Now, Owner, false, "demo", null));
        task.Apply(new DraftChallengeDispatched(task.Id, DomainId.New(), Node, Now, AgentModel.ChallengeDefault, AgentEffort.High));
        task.Apply(Verdict(task, ChallengeVerdict.DoNotPublish, must: 2));

        (task.Objective, task.AcceptanceCriteria, task.AgentContext, task.Type, task.State)
            .Should().Be((objective, (IReadOnlyList<string>)task.AcceptanceCriteria, context, type, state));
        task.AcceptanceCriteria.Should().Equal(criteria);
    }

    // ---- the gate ----

    [Fact]
    public void Nothing_asks_for_a_verdict_unless_the_caller_takes_part_in_the_gate()
    {
        TaskAggregate task = Draft();

        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        published.ChallengeSkipReason.Should().BeNull("every door that never took part publishes as it always did");
    }

    [Fact]
    public void With_the_requirement_on_a_draft_with_no_verdict_is_refused_naming_the_verb_the_flag_and_the_setting()
    {
        Action act = Publish(Draft());

        act.Should().Throw<DomainBusinessRuleException>()
            .Which.Message.Should().Contain("h9k task challenge").And.Contain("--skip-challenge")
            .And.Contain("h9k project set demo --require-challenge off");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_requirement_off_asks_for_nothing_and_records_no_reason(bool passReason)
    {
        TaskAggregate task = Draft();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner,
            challenge: new ChallengeGateInput(false, "demo", passReason ? "unneeded" : null));

        published.ChallengeSkipReason.Should().BeNull();
    }

    [Fact]
    public void A_do_not_publish_verdict_refuses_and_a_publish_as_written_passes()
    {
        TaskAggregate task = Draft();
        task.Apply(Verdict(task, ChallengeVerdict.DoNotPublish, must: 1));
        Publish(task).Should().Throw<DomainBusinessRuleException>().WithMessage("*do not publish*");

        task.Apply(Verdict(task, ChallengeVerdict.PublishAsWritten, park: 2));
        Publish(task).Should().NotThrow();
    }

    [Fact]
    public void A_publish_as_revised_holding_a_must_refuses_until_the_draft_is_revised_and_a_should_alone_never_does()
    {
        TaskAggregate withMust = Draft();
        withMust.Apply(Verdict(withMust, ChallengeVerdict.PublishAsRevised, must: 2, should: 1));
        Publish(withMust).Should().Throw<DomainBusinessRuleException>().WithMessage("*2 must changes*not been revised*");

        Revise(withMust);
        Publish(withMust).Should().NotThrow("revising is how a verdict gets applied");
        Revise(withMust);
        Publish(withMust).Should().NotThrow("any other latest verdict passes, including after later revisions");

        TaskAggregate shouldOnly = Draft();
        shouldOnly.Apply(Verdict(shouldOnly, ChallengeVerdict.PublishAsRevised, should: 3));
        Publish(shouldOnly).Should().NotThrow();
    }

    [Fact]
    public void A_failure_after_a_passing_verdict_does_not_reopen_the_gate_but_one_with_no_verdict_names_that()
    {
        TaskAggregate task = Draft();
        Publish(task).Should().Throw<DomainBusinessRuleException>().WithMessage("*no challenge verdict on record*");

        task.Apply(Failure(task, ChallengeFailureReason.TimedOut));
        Publish(task).Should().Throw<DomainBusinessRuleException>().WithMessage("*ended with no verdict*");

        task.Apply(Verdict(task, ChallengeVerdict.PublishAsWritten));
        task.Apply(Failure(task, ChallengeFailureReason.SessionError));
        Publish(task).Should().NotThrow("a failure replaces nothing");
    }

    [Fact]
    public void A_stated_reason_publishes_past_the_gate_and_is_recorded_trimmed()
    {
        TaskAggregate task = Draft();

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, challenge: new ChallengeGateInput(true, "demo", "  a typo fix  "));

        published.ChallengeSkipReason.Should().Be("a typo fix");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reason_is_refused_whether_or_not_the_gate_asks(string reason)
    {
        Publish(Draft(), required: true, skip: reason).Should().Throw<DomainValidationException>().WithMessage("*blank*");
        Publish(Draft(), required: false, skip: reason).Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_reason_passed_where_the_gate_asked_for_nothing_is_not_recorded()
    {
        TaskAggregate task = Draft();
        task.Apply(Verdict(task, ChallengeVerdict.PublishAsWritten));

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now, Owner, challenge: new ChallengeGateInput(true, "demo", "not needed"));

        published.ChallengeSkipReason.Should().BeNull();
    }

    [Theory]
    [InlineData("Spike")]
    [InlineData("PrReview")]
    public void The_requirement_never_applies_to_a_spike_or_a_pr_review_task(string type)
    {
        TaskType taskType = type == "Spike" ? TaskType.Spike : TaskType.PrReview;
        ChallengeGate.AppliesTo(taskType).Should().BeFalse();
        ChallengeGate.Shortfall(taskType, null, null, false, required: true).Should().BeNull();
    }
}
