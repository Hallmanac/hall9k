using FluentAssertions;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The plan's loop, with no database and no process: where a draft goes after a refinement session or a challenge, the cap
/// that parks, what a resume re-runs, and when the plan publishes and ends.
/// </summary>
public sealed class PlanDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private const int Cap = 2;

    private readonly Guid _projectId = DomainId.New();
    private readonly Guid _owner = DomainId.New();

    private PlanAggregate Started(PlanPhase phase, params Guid[] drafts)
    {
        PlanAggregate plan = new();
        plan.Apply(new PlanStarted(DomainId.New(), _projectId, drafts, phase, false, false, _owner, Now));
        return plan;
    }

    private static PlanAggregate Fold(PlanAggregate plan, IEnumerable<object> events)
    {
        foreach (object @event in events)
        {
            plan.ApplyEvent(@event);
        }

        return plan;
    }

    private static DraftChallengeRecorded Verdict(Guid draft, ChallengeVerdict verdict, int must = 0, int should = 0) => new(
        draft, DomainId.New(), DomainId.New(), verdict, ChallengeFailureReason.Unknown, must, should, 0,
        AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private static DraftChallengeRecorded FailedChallenge(Guid draft) => new(
        draft, null, DomainId.New(), ChallengeVerdict.Unknown, ChallengeFailureReason.TimedOut, 0, 0, 0,
        AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private PlanAggregate AtChallenge(Guid draft, int round = 1)
    {
        PlanAggregate plan = Started(PlanPhase.Challenge, draft);
        for (int next = 2; next <= round; next++)
        {
            plan.ApplyEvent(new PlanDraftMoved(
                plan.Id, _projectId, draft, PlanPhase.Challenge, next, PlanOutcome.Revised, RefinementPurpose.Unknown, false, null, Now));
        }

        return plan;
    }

    [Fact]
    public void A_draft_starts_at_refinement_in_round_zero_or_at_challenge_in_round_one()
    {
        Guid draft = DomainId.New();

        PlanDraftState refining = Started(PlanPhase.Refinement, draft).Drafts.Single();
        refining.Phase.Should().Be(PlanPhase.Refinement);
        refining.Round.Should().Be(0);
        refining.Purpose.Should().Be(RefinementPurpose.Refine);
        PlanDecider.NextStep(refining).Should().Be(PlanStepKind.DispatchRefinement);

        PlanDraftState challenging = Started(PlanPhase.Challenge, draft).Drafts.Single();
        challenging.Phase.Should().Be(PlanPhase.Challenge);
        challenging.Round.Should().Be(1);
        PlanDecider.NextStep(challenging).Should().Be(PlanStepKind.RequestChallenge);
    }

    [Theory]
    [InlineData("revised", "Revised")]
    [InlineData("unchanged", "Unchanged")]
    public void A_first_refinement_that_finishes_sends_the_draft_to_its_first_challenge(string word, string expected)
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = Started(PlanPhase.Refinement, draft);
        RefinementOutcome.TryParseWord(word, out RefinementOutcome outcome).Should().BeTrue();

        Fold(plan, PlanDecider.AfterRefinement(plan, plan.Drafts[0], RefinementPurpose.Refine, outcome, DomainId.New(), Now));

        PlanDraftState state = plan.Drafts[0];
        state.Phase.Should().Be(PlanPhase.Challenge);
        state.Round.Should().Be(1);
        state.Outcome.Value.Should().Be(expected);
        PlanDecider.NextStep(state).Should().Be(PlanStepKind.RequestChallenge);
    }

    [Fact]
    public void A_question_parks_the_plan_whatever_the_session_was_for()
    {
        foreach (RefinementPurpose purpose in new[] { RefinementPurpose.Refine, RefinementPurpose.ApplyVerdict, RefinementPurpose.AnswerVerdict })
        {
            Guid draft = DomainId.New();
            PlanAggregate plan = Started(PlanPhase.Refinement, draft);

            Fold(plan, PlanDecider.AfterRefinement(plan, plan.Drafts[0], purpose, RefinementOutcome.Question, DomainId.New(), Now));

            plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.Question);
            plan.IsParked.Should().BeTrue();
            PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.None);
        }
    }

    [Fact]
    public void Publish_as_written_makes_the_draft_ready_and_the_plan_publishes_it()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.PublishAsWritten), Cap, Now));

        plan.Drafts[0].Ready.Should().BeTrue();
        plan.Drafts[0].Outcome.Should().Be(PlanOutcome.PublishAsWritten);
        PlanDecider.DraftsToPublish(plan).Should().Equal(draft);
    }

    [Fact]
    public void Publish_as_revised_gets_one_apply_session_and_then_the_draft_is_ready_without_another_challenge()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.PublishAsRevised, must: 1), Cap, Now));

        plan.Drafts[0].Phase.Should().Be(PlanPhase.Refinement);
        plan.Drafts[0].Purpose.Should().Be(RefinementPurpose.ApplyVerdict);
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.DispatchRefinement);

        Fold(plan, PlanDecider.AfterRefinement(
            plan, plan.Drafts[0], RefinementPurpose.ApplyVerdict, RefinementOutcome.Revised, DomainId.New(), Now));

        plan.Drafts[0].Ready.Should().BeTrue();
        plan.Drafts[0].Round.Should().Be(1, "no further challenge");
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.None);
        PlanDecider.DraftsToPublish(plan).Should().Equal(draft);
    }

    [Theory]
    [InlineData("Unchanged")]
    public void An_apply_session_that_changed_nothing_parks_the_plan(string outcomeValue)
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);
        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.PublishAsRevised, should: 2), Cap, Now));
        RefinementOutcome outcome = outcomeValue;

        Fold(plan, PlanDecider.AfterRefinement(plan, plan.Drafts[0], RefinementPurpose.ApplyVerdict, outcome, DomainId.New(), Now));

        plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.VerdictNotApplied);
        plan.Drafts[0].Ready.Should().BeFalse();
    }

    [Fact]
    public void Do_not_publish_starts_the_next_round_with_a_session_that_answers_the_verdict_and_then_a_new_challenge()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.DoNotPublish, must: 1), Cap, Now));

        plan.Drafts[0].Phase.Should().Be(PlanPhase.Refinement);
        plan.Drafts[0].Purpose.Should().Be(RefinementPurpose.AnswerVerdict);
        plan.IsParked.Should().BeFalse();

        Fold(plan, PlanDecider.AfterRefinement(
            plan, plan.Drafts[0], RefinementPurpose.AnswerVerdict, RefinementOutcome.Revised, DomainId.New(), Now));

        plan.Drafts[0].Phase.Should().Be(PlanPhase.Challenge);
        plan.Drafts[0].Round.Should().Be(2);
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.RequestChallenge);
    }

    [Fact]
    public void An_answer_session_that_changed_nothing_parks_the_plan_rather_than_challenge_the_same_text_again()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);
        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.DoNotPublish, must: 1), Cap, Now));

        Fold(plan, PlanDecider.AfterRefinement(
            plan, plan.Drafts[0], RefinementPurpose.AnswerVerdict, RefinementOutcome.Unchanged, DomainId.New(), Now));

        plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.VerdictNotApplied);
    }

    [Fact]
    public void Do_not_publish_on_the_last_round_parks_instead_of_starting_another_and_a_resume_grants_one_more()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft, round: Cap);

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.DoNotPublish, must: 1), Cap, Now));

        plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.RoundsExhausted);
        plan.Drafts[0].Phase.Should().Be(PlanPhase.Challenge, "no answer session starts on the last round");
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.None);

        Fold(plan, [PlanDecider.Resume(plan, _owner, Now)]);

        plan.Drafts[0].ExtraRounds.Should().Be(1);
        plan.Drafts[0].IsParked.Should().BeFalse();
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.StartNextRound);

        Fold(plan, [PlanDecider.StartNextRound(plan, plan.Drafts[0], Now)]);
        plan.Drafts[0].Purpose.Should().Be(RefinementPurpose.AnswerVerdict);
        Fold(plan, PlanDecider.AfterRefinement(
            plan, plan.Drafts[0], RefinementPurpose.AnswerVerdict, RefinementOutcome.Revised, DomainId.New(), Now));
        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(draft, ChallengeVerdict.DoNotPublish, must: 1), Cap, Now));
        plan.Drafts[0].Round.Should().Be(3);
        plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.RoundsExhausted, "the granted round was the last one too");
    }

    [Fact]
    public void A_challenge_that_ended_with_no_verdict_parks_and_a_resume_asks_for_it_again_in_the_same_round()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = AtChallenge(draft);

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], FailedChallenge(draft), Cap, Now));

        plan.Drafts[0].ParkReason.Should().Be(PlanParkReason.ChallengeFailed);
        plan.Drafts[0].Outcome.Should().Be(PlanOutcome.ChallengeFailed);

        Fold(plan, [PlanDecider.Resume(plan, _owner, Now)]);

        plan.Drafts[0].Round.Should().Be(1);
        PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.RequestChallenge);
    }

    [Fact]
    public void A_failed_session_a_changed_draft_and_a_refused_revision_each_park_with_their_own_reason_and_resume_runs_the_session_again()
    {
        foreach (PlanParkReason reason in new[] { PlanParkReason.SessionFailed, PlanParkReason.DraftChanged, PlanParkReason.RevisionRefused })
        {
            Guid draft = DomainId.New();
            PlanAggregate plan = Started(PlanPhase.Refinement, draft);
            Guid session = DomainId.New();
            plan.ApplyEvent(new PlanRefinementDispatched(plan.Id, _projectId, draft, session, RefinementPurpose.Refine, Now));
            PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.AwaitSession);

            plan.ApplyEvent(PlanDecider.ParkOn(plan, draft, reason, session, Now));

            plan.Drafts[0].ParkReason.Should().Be(reason);
            plan.Drafts[0].RunningSessionId.Should().BeNull();
            plan.ApplyEvent(PlanDecider.Resume(plan, _owner, Now));
            PlanDecider.NextStep(plan.Drafts[0]).Should().Be(PlanStepKind.DispatchRefinement);
        }
    }

    [Fact]
    public void While_a_plan_is_parked_it_publishes_nothing_even_when_a_sibling_is_ready()
    {
        Guid ready = DomainId.New();
        Guid stuck = DomainId.New();
        PlanAggregate plan = Started(PlanPhase.Challenge, ready, stuck);
        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[0], Verdict(ready, ChallengeVerdict.PublishAsWritten), Cap, Now));
        PlanDecider.DraftsToPublish(plan).Should().BeEmpty("the second draft is not ready");

        Fold(plan, PlanDecider.AfterChallenge(plan, plan.Drafts[1], Verdict(stuck, ChallengeVerdict.PublishAsWritten), Cap, Now));
        PlanDecider.DraftsToPublish(plan).Should().Equal(ready, stuck);

        plan.ApplyEvent(PlanDecider.ParkOn(plan, stuck, PlanParkReason.PublishRefused, null, Now));
        PlanDecider.DraftsToPublish(plan).Should().BeEmpty();
    }

    [Fact]
    public void Publishing_a_parked_draft_by_hand_answers_the_park_and_the_plan_carries_on_without_a_resume()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        PlanAggregate plan = Started(PlanPhase.Challenge, first, second);
        plan.ApplyEvent(PlanDecider.ParkOn(plan, first, PlanParkReason.ChallengeFailed, null, Now));
        plan.IsParked.Should().BeTrue();

        plan.ApplyEvent(new PlanDraftPublished(plan.Id, _projectId, first, ByHand: true, Now));

        plan.IsParked.Should().BeFalse();
        plan.Drafts[0].Published.Should().BeTrue();
        PlanDecider.NextStep(plan.Drafts[1]).Should().Be(PlanStepKind.RequestChallenge);
    }

    [Fact]
    public void The_plan_ends_published_when_its_last_draft_does_and_cancelled_when_every_draft_was_abandoned()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        PlanAggregate plan = Started(PlanPhase.Challenge, first, second);
        PlanDecider.End(plan, Now).Should().BeNull();

        plan.ApplyEvent(new PlanDraftPublished(plan.Id, _projectId, first, false, Now));
        PlanDecider.End(plan, Now).Should().BeNull("one draft is still open");

        plan.ApplyEvent(new PlanDraftAbandoned(plan.Id, _projectId, second, Now));
        PlanDecider.End(plan, Now, [first]).Should().BeOfType<PlanPublished>()
            .Which.WithoutTrackerItem.Should().Equal(first);

        Guid only = DomainId.New();
        PlanAggregate abandoned = Started(PlanPhase.Challenge, only);
        abandoned.ApplyEvent(new PlanDraftAbandoned(abandoned.Id, _projectId, only, Now));
        PlanDecider.End(abandoned, Now).Should().BeOfType<PlanCancelled>();
    }

    [Fact]
    public void Resume_needs_a_live_parked_plan_and_cancel_needs_a_live_plan_and_a_reason()
    {
        Guid draft = DomainId.New();
        PlanAggregate plan = Started(PlanPhase.Refinement, draft);

        Action notParked = () => PlanDecider.Resume(plan, _owner, Now);
        notParked.Should().Throw<DomainConflictException>().WithMessage("*not parked*");

        Action blank = () => PlanDecider.Cancel(plan, "  ", _owner, Now);
        blank.Should().Throw<DomainValidationException>().WithMessage("*reason*");

        plan.ApplyEvent(PlanDecider.Cancel(plan, "the idea changed", _owner, Now));
        plan.State.Should().Be(PlanState.Cancelled);
        plan.CancelReason.Should().Be("the idea changed");

        Action ended = () => PlanDecider.Cancel(plan, "again", _owner, Now);
        ended.Should().Throw<DomainConflictException>();
        Action resumeEnded = () => PlanDecider.Resume(plan, _owner, Now);
        resumeEnded.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void A_plan_event_is_not_a_task_event_and_never_leaves_its_node()
    {
        foreach (Type type in typeof(PlanStarted).Assembly.GetTypes()
                     .Where(type => type.Namespace == typeof(PlanStarted).Namespace && type.IsSealed && type.Name.StartsWith("Plan", StringComparison.Ordinal)))
        {
            Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry.ClassificationOf(type)
                .Should().Be(Hall9k.Domain.Infrastructure.Persistence.EventScope.NodeScoped, $"{type.Name} is a plan record");
        }

        Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry.ClassificationOf(typeof(DraftRefinementDispatched))
            .Should().Be(Hall9k.Domain.Infrastructure.Persistence.EventScope.ProjectScoped);
        Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry.ClassificationOf(typeof(DraftRefinementRecorded))
            .Should().Be(Hall9k.Domain.Infrastructure.Persistence.EventScope.ProjectScoped);
    }
}
