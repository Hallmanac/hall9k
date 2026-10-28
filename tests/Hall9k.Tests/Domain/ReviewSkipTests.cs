using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>h9k task skip-to-pr</c>'s domain half (idea 6e6d0200, Card 3): when it is admitted, what the
/// recorded skipped list says at each door, and where the event leaves the run.
/// </summary>
public sealed class ReviewSkipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static (RunAggregate Run, Guid Id) Dispatched(ReviewStageComposition? composition = null)
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), Guid.Empty, DomainId.New(), 1, DomainId.New(), "/wt/x", "task/x",
            ExecutorMode.Subscription, Now, ReviewStageComposition: composition));
        return (run, id);
    }

    private static TaskDetails Task(TaskType? type = null, bool interactive = true) => new()
    {
        Id = DomainId.New(),
        Type = type ?? TaskType.Feature,
        InteractiveModeEnabled = interactive,
    };

    // ---- admission ----

    [Fact]
    public void A_claim_still_building_is_admitted_from_the_build()
    {
        (RunAggregate run, _) = Dispatched();

        ReviewSkipRules.Admit(Task(), run).Should().Be(ReviewSkipEntry.FromBuild);
    }

    [Fact]
    public void A_review_park_is_admitted_whether_an_interactive_boundary_or_any_other_park()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewParked(id, "Interactive mode is on for this task.", Now, IsInteractiveGate: true));
        ReviewSkipRules.Admit(Task(), run).Should().Be(ReviewSkipEntry.FromPark);

        (RunAggregate capped, Guid cappedId) = Dispatched();
        capped.Apply(new AgentSessionCompleted(cappedId, Now));
        capped.Apply(new ReviewDispatched(cappedId, DomainId.New(), 1, 5001, Now, Now));
        capped.Apply(new ReviewParked(cappedId, "Cycle cap spent.", Now));
        ReviewSkipRules.Admit(Task(), capped).Should().Be(ReviewSkipEntry.FromPark);
    }

    [Fact]
    public void A_park_waiting_on_a_reply_to_a_human_reviewer_is_refused_and_names_the_reply_choices()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new HumanThreadReplyParked(id, [], Now));
        run.Apply(new ReviewParked(id, "A drafted reply is waiting on a decision.", Now));

        Action admit = () => ReviewSkipRules.Admit(Task(), run);

        admit.Should().Throw<DomainConflictException>()
            .WithMessage("*--post-reply-as-written*--post-reply*--post-nothing*");
    }

    [Fact]
    public void A_task_with_interactive_mode_off_is_refused()
    {
        (RunAggregate run, _) = Dispatched();

        Action admit = () => ReviewSkipRules.Admit(Task(interactive: false), run);

        admit.Should().Throw<DomainConflictException>().WithMessage("*not in interactive mode*");
    }

    [Fact]
    public void A_pr_review_task_is_refused_because_it_opens_no_pull_request_of_its_own()
    {
        (RunAggregate run, _) = Dispatched();

        Action admit = () => ReviewSkipRules.Admit(Task(TaskType.PrReview), run);

        admit.Should().Throw<DomainConflictException>().WithMessage("*pr-review task*opens no pull request*");
    }

    [Fact]
    public void A_spike_is_refused_because_it_opens_no_pull_request_of_its_own()
    {
        (RunAggregate run, _) = Dispatched();

        Action admit = () => ReviewSkipRules.Admit(Task(TaskType.Spike), run);

        admit.Should().Throw<DomainConflictException>().WithMessage("*spike*opens no pull request*");
    }

    [Fact]
    public void While_a_gate_or_review_session_is_live_it_refuses_and_says_the_next_park_will_offer_it()
    {
        (RunAggregate verifying, Guid verifyingId) = Dispatched();
        verifying.Apply(new AgentSessionCompleted(verifyingId, Now));
        (RunAggregate reviewing, Guid reviewingId) = Dispatched();
        reviewing.Apply(new AgentSessionCompleted(reviewingId, Now));
        reviewing.Apply(new ReviewDispatched(reviewingId, DomainId.New(), 1, 5001, Now, Now));

        foreach (RunAggregate run in new[] { verifying, reviewing })
        {
            Action admit = () => ReviewSkipRules.Admit(Task(), run);

            admit.Should().Throw<DomainConflictException>().WithMessage("*next park*");
        }
    }

    [Fact]
    public void At_the_gates_to_pull_request_boundary_it_refuses_and_names_review_proceed()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new ReviewSettled(id, 1, ReviewSettlement.Clean, 0, 0, 0, Now));
        run.Apply(new ReviewParked(id, "review is merge-ready", Now, IsInteractiveGate: true));
        TaskDetails task = Task();

        Action admit = () => ReviewSkipRules.Admit(task, run);

        admit.Should().Throw<DomainConflictException>()
            .WithMessage($"*nothing to skip*h9k review proceed {task.Id}*");
    }

    // ---- the skipped list ----

    [Fact]
    public void From_the_build_it_names_the_gates_both_tracks_the_final_pass_and_the_settling_gate()
    {
        (RunAggregate run, _) = Dispatched();

        ReviewSkipPlan.Compute(run).Should().Equal(
            ReviewSkipPlan.Gates,
            ReviewSkipPlan.Track(ReviewLens.Conformance),
            ReviewSkipPlan.Track(ReviewLens.Adversarial),
            ReviewSkipPlan.FinalPass,
            ReviewSkipPlan.SettlingGate);
    }

    [Fact]
    public void The_tracks_are_read_from_the_lens_list_so_a_new_lens_would_be_named_without_an_edit()
    {
        (RunAggregate run, _) = Dispatched();

        ReviewSkipPlan.Compute(run).Where(item => item.Contains("review track")).Should().HaveCount(
            ReviewLens.CycleLenses.Count);
    }

    [Fact]
    public void From_a_park_after_the_gates_it_leaves_the_gates_out()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewParked(id, "Interactive mode is on", Now, IsInteractiveGate: true));

        ReviewSkipPlan.Compute(run).Should().NotContain(ReviewSkipPlan.Gates)
            .And.Contain(ReviewSkipPlan.Track(ReviewLens.Conformance));
    }

    [Fact]
    public void From_a_park_raised_before_the_gates_it_still_names_them()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new ReviewParked(id, "A thread dispute, raised before the gates", Now));

        ReviewSkipPlan.Compute(run).Should().Contain(ReviewSkipPlan.Gates);
    }

    [Fact]
    public void A_track_that_already_concluded_is_not_named_because_it_would_not_have_run_again()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now));
        run.Apply(new ReviewParked(id, "Adversarial cap spent", Now));

        ReviewSkipPlan.Compute(run).Should()
            .Contain(ReviewSkipPlan.Track(ReviewLens.Adversarial))
            .And.NotContain(ReviewSkipPlan.Track(ReviewLens.Conformance));
    }

    [Fact]
    public void A_composition_that_runs_one_track_names_only_that_track()
    {
        (RunAggregate run, _) = Dispatched(ReviewStageComposition.AdversarialOnly);

        ReviewSkipPlan.Compute(run).Should()
            .Contain(ReviewSkipPlan.Track(ReviewLens.Adversarial))
            .And.NotContain(ReviewSkipPlan.Track(ReviewLens.Conformance));
    }

    [Fact]
    public void A_composition_with_no_review_or_no_final_pass_leaves_the_final_pass_out()
    {
        (RunAggregate none, _) = Dispatched(ReviewStageComposition.None);
        (RunAggregate skipFinal, _) = Dispatched(ReviewStageComposition.SkipFinalPass);

        ReviewSkipPlan.Compute(none).Should().Equal(ReviewSkipPlan.Gates, ReviewSkipPlan.SettlingGate);
        ReviewSkipPlan.Compute(skipFinal).Should().NotContain(ReviewSkipPlan.FinalPass);
    }

    [Fact]
    public void A_park_after_the_final_pass_already_settled_leaves_the_final_pass_out()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Adversarial, 1, ReviewSettlement.Clean, [], Now));
        run.Apply(new ReviewSettled(id, 1, ReviewSettlement.Clean, 0, 0, 0, Now));
        run.Apply(new ReviewParked(id, "park", Now));

        ReviewSkipPlan.Compute(run).Should().Equal(ReviewSkipPlan.SettlingGate);
    }

    [Fact]
    public void A_park_at_the_fix_to_re_review_boundary_leaves_out_the_gates_the_fix_already_passed()
    {
        (RunAggregate run, Guid id) = AfterAFixLanded();
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewParked(id, "Interactive mode is on for this task.", Now, IsInteractiveGate: true));

        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.Reverify);
        ReviewSkipPlan.Compute(run).Should().NotContain(ReviewSkipPlan.Gates);
    }

    [Fact]
    public void A_park_after_a_fix_but_before_its_gates_ran_still_names_the_gates()
    {
        (RunAggregate run, _) = AfterAFixLanded();
        run.Apply(new ReviewParked(run.Id, "Parked before the fix's gates ran.", Now));

        ReviewSkipPlan.Compute(run).Should().Contain(ReviewSkipPlan.Gates);
    }

    [Fact]
    public void A_park_in_front_of_the_final_pass_still_names_it_because_it_has_not_run()
    {
        (RunAggregate run, Guid id) = AfterAFixLanded();
        run.Apply(new VerificationPassed(id, Now));
        ReadCleanByEveryTrack(run, id, 2, ReviewMode.Verify);
        run.Apply(new ReviewParked(id, "gates passed and the mandatory final review pass is ready", Now, IsInteractiveGate: true));

        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.Settling);
        ReviewSkipPlan.Compute(run).Should().Contain(ReviewSkipPlan.FinalPass);
    }

    [Fact]
    public void A_settling_park_after_a_clean_full_read_with_nothing_owed_leaves_the_final_pass_out()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        ReadCleanByEveryTrack(run, id, 1, ReviewMode.Discovery);
        run.Apply(new ReviewParked(id, "Lifetime budget spent.", Now));

        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.Settling);
        ReviewSkipPlan.Compute(run).Should().Equal(ReviewSkipPlan.SettlingGate);
    }

    private static void ReadCleanByEveryTrack(RunAggregate run, Guid id, int cycle, ReviewMode mode)
    {
        foreach (ReviewLens lens in ReviewLens.CycleLenses)
        {
            run.Apply(new ReviewDispatched(id, DomainId.New(), cycle, 5001, Now, Now, null, lens, mode));
            run.Apply(new ReviewPassCompleted(id, cycle, lens, ReviewVerdict.MergeReady, Now, [], mode));
            run.Apply(new ReviewTrackConcluded(id, lens, cycle, ReviewSettlement.Clean, [], Now));
        }
    }

    /// <summary>A run whose first review cycle ordered a fix and whose fix session has just completed.</summary>
    private static (RunAggregate Run, Guid Id) AfterAFixLanded()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        ReadCleanByEveryTrack(run, id, 1, ReviewMode.Discovery);
        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 1, 5003, Now, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now));
        return (run, id);
    }

    // ---- what the event does to the run ----

    [Fact]
    public void The_event_leaves_the_run_under_review_at_merge_ready_overridden_with_the_gate_cleared()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewParked(id, "Interactive mode is on", Now, IsInteractiveGate: true));

        run.Apply(new ReviewSkippedToPullRequest(id, DomainId.New(), Now, "docs only", false, ReviewSkipPlan.Compute(run)));

        run.State.Should().Be(RunState.UnderReview);
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Overridden);
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.InteractiveGateCleared.Should().BeTrue("no further interactive park is raised for this run");
        run.ParkedIsInteractiveGate.Should().BeFalse();
    }

    [Fact]
    public void The_event_applies_from_the_build_too_and_a_later_run_starts_without_it()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new ReviewSkippedToPullRequest(id, DomainId.New(), Now, null, false, ReviewSkipPlan.Compute(run)));
        run.ReviewSettlement.Should().Be(ReviewSettlement.Overridden);

        (RunAggregate later, _) = Dispatched();

        later.ReviewSettlement.Should().Be(ReviewSettlement.Unknown);
        later.InteractiveGateCleared.Should().BeFalse();
        later.ReviewPhase.Should().Be(ReviewPhase.None);
    }

    [Fact]
    public void A_stream_without_the_event_replays_unchanged()
    {
        (RunAggregate run, Guid id) = Dispatched();
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewSettled(id, 1, ReviewSettlement.Clean, 0, 0, 0, Now));

        run.ReviewSettlement.Should().Be(ReviewSettlement.Clean);
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.GatesPassed.Should().BeTrue();
    }
}
