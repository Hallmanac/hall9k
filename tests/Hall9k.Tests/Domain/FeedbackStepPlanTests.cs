using FluentAssertions;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What a plan that carries a task's feedback step does differently from an ordinary one, decided over the plan alone: one
/// challenge round, a session that judges a publish as revised verdict, a closing word for a task that should not run, and one
/// challenge ever.
/// </summary>
public sealed class FeedbackStepPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Draft = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    private static PlanAggregate Plan(bool feedback)
    {
        PlanAggregate plan = new();
        plan.Apply(new PlanStarted(
            DomainId.New(), Project, [Draft], PlanPhase.Refinement, false, false, Owner, Now,
            FeedbackStepTaskId: feedback ? Draft : null));
        return plan;
    }

    private static DraftChallengeRecorded Verdict(ChallengeVerdict verdict, int must = 0) =>
        new(Draft, DomainId.New(), DomainId.New(), verdict, ChallengeFailureReason.Unknown, must, 0, 0,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now);

    /// <summary>The plan after its one refinement: the draft is in challenge at round one.</summary>
    private static (PlanAggregate Plan, PlanDraftState Draft) AtChallenge(bool feedback)
    {
        PlanAggregate plan = Plan(feedback);
        PlanDraftState draft = plan.Draft(Draft)!;
        foreach (object @event in PlanDecider.AfterRefinement(plan, draft, draft.Purpose, RefinementOutcome.Revised, DomainId.New(), Now))
        {
            Apply(plan, @event);
        }

        return (plan, plan.Draft(Draft)!);
    }

    private static void Apply(PlanAggregate plan, object @event)
    {
        switch (@event)
        {
            case PlanDraftMoved moved: plan.Apply(moved); break;
            case PlanParked parked: plan.Apply(parked); break;
            case PlanChallengeRequested requested: plan.Apply(requested); break;
            default: throw new InvalidOperationException(@event.GetType().Name);
        }
    }

    [Fact]
    public void A_feedback_plan_starts_its_draft_at_the_feedback_refinement_and_an_ordinary_one_at_the_plain_refine()
    {
        Plan(feedback: true).Draft(Draft)!.Purpose.Should().Be(RefinementPurpose.FeedbackRefine);
        Plan(feedback: true).IsFeedbackStep.Should().BeTrue();
        Plan(feedback: false).Draft(Draft)!.Purpose.Should().Be(RefinementPurpose.Refine);
        Plan(feedback: false).IsFeedbackStep.Should().BeFalse();
    }

    [Fact]
    public void A_do_not_publish_verdict_parks_a_feedback_plan_at_once_because_its_cap_is_one_round()
    {
        (PlanAggregate plan, PlanDraftState draft) = AtChallenge(feedback: true);

        IReadOnlyList<object> next = PlanDecider.AfterChallenge(
            plan, draft, Verdict(ChallengeVerdict.DoNotPublish, must: 2), PlanRoundCap.FeedbackStep, Now);

        next.OfType<PlanParked>().Should().ContainSingle().Which.Reason.Should().Be(PlanParkReason.RoundsExhausted);
    }

    [Fact]
    public void A_publish_as_revised_verdict_sends_a_feedback_plan_to_the_session_that_judges_it_and_an_ordinary_plan_to_the_one_that_applies_it()
    {
        (PlanAggregate feedbackPlan, PlanDraftState feedbackDraft) = AtChallenge(feedback: true);
        (PlanAggregate ordinaryPlan, PlanDraftState ordinaryDraft) = AtChallenge(feedback: false);

        PlanDecider.AfterChallenge(feedbackPlan, feedbackDraft, Verdict(ChallengeVerdict.PublishAsRevised, 1), PlanRoundCap.FeedbackStep, Now)
            .OfType<PlanDraftMoved>().Single().Purpose.Should().Be(RefinementPurpose.JudgeVerdict);
        PlanDecider.AfterChallenge(ordinaryPlan, ordinaryDraft, Verdict(ChallengeVerdict.PublishAsRevised, 1), PlanRoundCap.Default, Now)
            .OfType<PlanDraftMoved>().Single().Purpose.Should().Be(RefinementPurpose.ApplyVerdict);
    }

    [Theory]
    [InlineData("Revised")]
    [InlineData("Unchanged")]
    public void A_session_that_judged_the_verdict_leaves_the_draft_ready_whether_or_not_it_revised_anything(string outcome)
    {
        (PlanAggregate plan, PlanDraftState draft) = AtChallenge(feedback: true);
        foreach (object @event in PlanDecider.AfterChallenge(
                     plan, draft, Verdict(ChallengeVerdict.PublishAsRevised, 1), PlanRoundCap.FeedbackStep, Now))
        {
            Apply(plan, @event);
        }

        PlanDraftState judging = plan.Draft(Draft)!;
        judging.Purpose.Should().Be(RefinementPurpose.JudgeVerdict);

        IReadOnlyList<object> after = PlanDecider.AfterRefinement(
            plan, judging, RefinementPurpose.JudgeVerdict, (RefinementOutcome)outcome, DomainId.New(), Now);

        PlanDraftMoved moved = after.OfType<PlanDraftMoved>().Single();
        moved.Ready.Should().BeTrue("declining every change with a reason is the session's call, and the publish gate counts it as the verdict applied");
        moved.Phase.Should().Be(PlanPhase.Challenge, "and there is no second challenge");
    }

    [Fact]
    public void A_session_that_concludes_the_task_should_not_run_parks_a_feedback_plan_and_an_ordinary_plan_reads_it_as_a_question()
    {
        PlanAggregate feedbackPlan = Plan(feedback: true);
        PlanAggregate ordinaryPlan = Plan(feedback: false);

        PlanDecider.AfterRefinement(feedbackPlan, feedbackPlan.Draft(Draft)!, RefinementPurpose.FeedbackRefine, RefinementOutcome.ShouldNotRun, DomainId.New(), Now)
            .OfType<PlanParked>().Single().Reason.Should().Be(PlanParkReason.ShouldNotRun);
        PlanDecider.AfterRefinement(ordinaryPlan, ordinaryPlan.Draft(Draft)!, RefinementPurpose.Refine, RefinementOutcome.ShouldNotRun, DomainId.New(), Now)
            .OfType<PlanParked>().Single().Reason.Should().Be(PlanParkReason.Question);
    }

    [Fact]
    public void A_feedback_plan_may_request_one_challenge_and_never_a_second_however_it_came_back_to_challenge()
    {
        (PlanAggregate plan, PlanDraftState draft) = AtChallenge(feedback: true);
        PlanDecider.MayRequestChallenge(plan, draft).Should().BeTrue();

        plan.Apply(new PlanChallengeRequested(plan.Id, Project, Draft, 1, Now));
        PlanDraftState asked = plan.Draft(Draft)!;
        asked.ChallengesRequested.Should().Be(1);
        // A person's resume, or any path back to challenge, still reads the count the stream carries.
        PlanDecider.MayRequestChallenge(plan, asked).Should().BeFalse();
        PlanDecider.ParkOneChallengeOnly(plan, asked, Now).Reason.Should().Be(PlanParkReason.OneChallengeOnly);

        (PlanAggregate ordinary, PlanDraftState ordinaryDraft) = AtChallenge(feedback: false);
        ordinary.Apply(new PlanChallengeRequested(ordinary.Id, Project, Draft, 1, Now));
        PlanDecider.MayRequestChallenge(ordinary, ordinary.Draft(Draft)!).Should().BeTrue("an ordinary plan's rounds are its own cap's business");
        ordinaryDraft.Should().NotBeNull();
    }

    [Fact]
    public void The_closed_words_a_feedback_plan_adds_round_trip()
    {
        ((RefinementPurpose)"JudgeVerdict").Should().Be(RefinementPurpose.JudgeVerdict);
        ((RefinementPurpose)"FeedbackRefine").Should().Be(RefinementPurpose.FeedbackRefine);
        RefinementOutcome.TryParseWord("should-not-run", out RefinementOutcome dashed).Should().BeTrue();
        RefinementOutcome.TryParseWord("Should not run", out RefinementOutcome spaced).Should().BeTrue();
        (dashed, spaced).Should().Be((RefinementOutcome.ShouldNotRun, RefinementOutcome.ShouldNotRun));
        ((PlanParkReason)"ShouldNotRun").Should().Be(PlanParkReason.ShouldNotRun);
        ((PlanParkReason)"RevisionBeyondText").Should().Be(PlanParkReason.RevisionBeyondText);
        ((PlanParkReason)"OneChallengeOnly").Should().Be(PlanParkReason.OneChallengeOnly);
        ((RefinementFailureReason)"RevisionBeyondText").Should().Be(RefinementFailureReason.RevisionBeyondText);
    }
}
