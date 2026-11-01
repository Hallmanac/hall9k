using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The run's side of the human gate: which facts an answer at the gate sets and which an answer at any
/// other park leaves alone, and what a fix at the gate reopens. These are the aggregate rules the daemon's
/// gate check and the CLI's levers stand on.
/// </summary>
public sealed class HumanGateRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const string ReviewedTip = "1111111111111111111111111111111111111111";

    /// <summary>A run whose review settled merge-ready after a clean cycle 1 and a final pass, ready for the boundary.</summary>
    private static (RunAggregate Run, Guid Id) Settled(ReviewSettlement? settlement = null)
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), Guid.Empty, DomainId.New(), 1, DomainId.New(), "/wt/x", "task/x",
            ExecutorMode.Subscription, Now));
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewSettled(id, 1, settlement ?? ReviewSettlement.Clean, 0, 0, 0, Now));
        return (run, id);
    }

    private static void ParkAtTheGate(RunAggregate run, Guid id, string? tip = ReviewedTip, ReviewPhase? from = null) =>
        run.Apply(new ReviewParked(
            id, "This task has a human gate.", Now, IsInteractiveGate: true, IsHumanGate: true, HumanGateTipSha: tip));

    [Fact]
    public void A_gate_park_is_an_interactive_gate_from_merge_ready_that_records_the_reviewed_tip()
    {
        (RunAggregate run, Guid id) = Settled();
        ParkAtTheGate(run, id);

        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedIsHumanGate.Should().BeTrue();
        run.ParkedIsInteractiveGate.Should().BeTrue("every existing interactive lever applies to the gate");
        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.HumanGateTipSha.Should().Be(ReviewedTip);
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
    }

    [Fact]
    public void A_gate_park_recorded_from_settling_is_still_the_merge_ready_boundary_and_withdraws_a_human_ended_loop()
    {
        (RunAggregate run, Guid id) = Settled();
        ParkAtTheGate(run, id);
        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "ship it", Now, DomainId.New()));
        run.HumanEndedTheLoop.Should().BeTrue();
        run.ReviewPhase.Should().Be(ReviewPhase.Settling);

        // The daemon found uncommitted work before the gates ran and sent the run back to the gate.
        ParkAtTheGate(run, id);

        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.HumanEndedTheLoop.Should().BeFalse();
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None, "a fresh gate park withdraws the earlier answer");
    }

    [Fact]
    public void Proceed_at_the_gate_is_the_answer_that_checks_the_tip_and_records_who_approved_and_when()
    {
        (RunAggregate run, Guid id) = Settled();
        ParkAtTheGate(run, id);
        Guid approver = DomainId.New();

        run.Apply(new ReviewBoundaryApproved(id, Now.AddHours(3), approver));

        run.HumanGateAnswer.Should().Be(HumanGateAnswer.Proceeded);
        run.HumanGateAnsweredAt.Should().Be(Now.AddHours(3));
        run.HumanGateAnsweredByOwnerId.Should().Be(approver);

        // The daemon found the branch had moved and sent the run back here: the approval no longer stands.
        ParkAtTheGate(run, id);
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.HumanGateAnsweredAt.Should().BeNull("task show must not say the stop is approved while it is parked again");
        run.HumanGateAnsweredByOwnerId.Should().BeNull();
        run.Apply(new ReviewBoundaryApproved(id, Now.AddHours(3), approver));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.ParkedIsHumanGate.Should().BeFalse();
        run.InteractiveGateCleared.Should().BeTrue("one answer releases both the gate and interactive mode's boundary");
    }

    [Fact]
    public void A_merge_ready_resolve_at_an_earlier_park_does_not_answer_the_gate()
    {
        (RunAggregate run, Guid id) = Settled();
        // An earlier, ordinary park (a cap) resolved merge-ready: that is not an answer at the gate.
        run.Apply(new ReviewParked(id, "Cycle cap spent.", Now));
        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "fine", Now, DomainId.New()));

        run.HumanEndedTheLoop.Should().BeTrue();
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None, "only an answer at this boundary releases it");
    }

    [Fact]
    public void Merge_ready_at_the_gate_owns_a_moved_tip_and_settles_as_a_human_ending()
    {
        (RunAggregate run, Guid id) = Settled();
        ParkAtTheGate(run, id);

        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "my own commits", Now, DomainId.New()));

        run.HumanGateAnswer.Should().Be(HumanGateAnswer.ResolvedMergeReady);
        run.HumanEndedTheLoop.Should().BeTrue();
        run.ReviewPhase.Should().Be(ReviewPhase.Settling);
    }

    [Fact]
    public void Needs_fixes_at_the_gate_withdraws_the_tip_and_the_answer_and_clears_an_earlier_human_ending()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewParked(id, "Cycle cap spent.", Now));
        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "fine", Now, DomainId.New()));
        run.HumanEndedTheLoop.Should().BeTrue();
        ParkAtTheGate(run, id);

        run.Apply(new ReviewParkResolved(id, ReviewVerdict.NeedsFixes, "rename the thing", Now, DomainId.New()));

        run.HumanEndedTheLoop.Should().BeFalse("nothing cleared this before, and the fix would settle with no review pass");
        run.HumanGateTipSha.Should().BeNull();
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded);
        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
    }

    [Fact]
    public void A_hand_fix_at_the_gate_reopens_review_like_a_fix_sessions_commits_whatever_ended_the_loop_before()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewParked(id, "Cycle cap spent.", Now));
        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "fine", Now, DomainId.New()));
        ParkAtTheGate(run, id);
        int cycle = run.ReviewCycle;

        run.Apply(new ReviewHumanFixApplied(id, cycle, "2222222222222222222222222222222222222222", null, false, Now, DomainId.New()));

        run.ReviewPhase.Should().Be(ReviewPhase.Reverify);
        run.LastReviewVerdict.Should().Be(
            ReviewVerdict.NeedsFixes, "a merge-ready verdict here would let the settle short-circuit read the fix as already reviewed");
        run.HumanEndedTheLoop.Should().BeFalse();
        run.FixDispatchedThisCycle.Should().BeTrue();
        run.FinalFullPassRounds.Should().Be(0);
        run.HumanGateTipSha.Should().BeNull("the next settled review records a fresh tip");
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.ParkedIsHumanGate.Should().BeFalse();
    }

    [Fact]
    public void Interactive_modes_own_pull_request_park_is_held_to_the_same_tip_rules_as_the_gate()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewParked(id, "Interactive mode is on.", Now, IsInteractiveGate: true, HumanGateTipSha: ReviewedTip));

        run.ParkedIsHumanGate.Should().BeFalse("this is not the task's gate");
        run.ParkedAtPullRequestBoundary.Should().BeTrue();
        run.HumanGateTipSha.Should().Be(ReviewedTip);

        run.Apply(new ReviewBoundaryApproved(id, Now, DomainId.New()));
        run.HumanGateAnswer.Should().Be(
            HumanGateAnswer.Proceeded, "a proceed here is checked against the tip like the gate's, so commits made at this park are not opened unread");

        // The daemon found the branch had moved and sent the run back to an interactive park of the same kind.
        run.Apply(new ReviewParked(id, "The pull request was not opened.", Now, IsInteractiveGate: true, HumanGateTipSha: ReviewedTip));
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.PullRequestBoundaryParkedAsHumanGate.Should().BeFalse();
        run.Apply(new ReviewHumanFixApplied(id, run.ReviewCycle, "2222222222222222222222222222222222222222", null, false, Now, DomainId.New()));
        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        run.ReviewPhase.Should().Be(ReviewPhase.Reverify);
    }

    /// <summary>
    /// Interactive mode's own gates-to-pull-request park sent back from Settling (a merge-ready resolve that met
    /// uncommitted work) is still that boundary, so the hand-fix lever its reason names stays open.
    /// </summary>
    [Fact]
    public void Interactive_modes_pull_request_park_sent_back_from_settling_stays_the_boundary()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewParked(id, "Interactive mode is on.", Now, IsInteractiveGate: true, HumanGateTipSha: ReviewedTip));
        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "ship it", Now, DomainId.New()));
        run.ReviewPhase.Should().Be(ReviewPhase.Settling);

        run.Apply(new ReviewParked(id, "The pull request was not opened.", Now, IsInteractiveGate: true, HumanGateTipSha: ReviewedTip));

        run.ParkedAtPullRequestBoundary.Should().BeTrue("the hand-fix lever the reason names must still be accepted");
        run.ParkedFromReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.HumanEndedTheLoop.Should().BeFalse();
    }

    /// <summary>
    /// A park raised later in Settling (the rebase recovery cap, say) after a merge-ready answer at the boundary,
    /// then redirected: the answer and the human ending are withdrawn so the fix is reviewed, and a run with no
    /// such answer keeps the earlier behaviour of settling on the human's merge-ready.
    /// </summary>
    [Fact]
    public void A_redirect_at_a_later_settling_park_withdraws_a_gate_answer_but_leaves_an_ungated_human_ending_alone()
    {
        (RunAggregate gated, Guid gatedId) = Settled();
        ParkAtTheGate(gated, gatedId);
        gated.Apply(new ReviewParkResolved(gatedId, ReviewVerdict.MergeReady, "ship it", Now, DomainId.New()));
        gated.HumanGateAnswer.Should().Be(HumanGateAnswer.ResolvedMergeReady);
        gated.Apply(new ReviewParked(gatedId, "The rebase recovery cap is spent.", Now));

        gated.Apply(new ReviewParkResolved(gatedId, ReviewVerdict.NeedsFixes, "fix the conflict", Now, DomainId.New()));

        gated.HumanEndedTheLoop.Should().BeFalse("the fix must be reviewed, not settled on the earlier merge-ready");
        gated.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        gated.HumanGateTipSha.Should().BeNull();

        (RunAggregate ungated, Guid ungatedId) = Settled();
        ungated.Apply(new ReviewParked(ungatedId, "Cycle cap spent.", Now));
        ungated.Apply(new ReviewParkResolved(ungatedId, ReviewVerdict.MergeReady, "fine", Now, DomainId.New()));
        ungated.Apply(new ReviewParked(ungatedId, "The rebase recovery cap is spent.", Now));

        ungated.Apply(new ReviewParkResolved(ungatedId, ReviewVerdict.NeedsFixes, "fix the conflict", Now, DomainId.New()));

        ungated.HumanEndedTheLoop.Should().BeTrue("a task with no answer at the boundary moves through review as it always did");
    }

    /// <summary>Whether a run was ever held at the boundary survives the levers that clear the tip and the answer.</summary>
    [Fact]
    public void Having_been_held_at_the_boundary_is_remembered_after_a_hand_fix_clears_the_tip_and_the_answer()
    {
        (RunAggregate run, Guid id) = Settled();
        run.HasParkedAtPullRequestBoundary.Should().BeFalse();
        ParkAtTheGate(run, id);

        run.Apply(new ReviewHumanFixApplied(id, run.ReviewCycle, "2222222222222222222222222222222222222222", null, false, Now, DomainId.New()));

        run.HumanGateTipSha.Should().BeNull();
        run.HasParkedAtPullRequestBoundary.Should().BeTrue("an owner may still have a product up in the worktree");
    }

    [Fact]
    public void A_hand_fix_at_an_ordinary_park_leaves_the_verdict_exactly_as_the_reviewers_left_it()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), Guid.Empty, DomainId.New(), 1, DomainId.New(), "/wt/x", "task/x",
            ExecutorMode.Subscription, Now));
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewParked(id, "Interactive mode is on.", Now, IsInteractiveGate: true));

        run.Apply(new ReviewHumanFixApplied(id, 1, null, null, false, Now, DomainId.New()));

        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
    }

    [Fact]
    public void Skipping_to_the_pull_request_is_the_owners_recorded_override_of_the_gate()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewSkippedToPullRequest(id, DomainId.New(), Now, "ship it", false, ["the final pass"]));

        run.HumanGateAnswer.Should().Be(HumanGateAnswer.Skipped);
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
    }

    [Fact]
    public void A_fix_session_dispatch_spends_the_answer_and_the_tip()
    {
        (RunAggregate run, Guid id) = Settled();
        ParkAtTheGate(run, id);
        run.Apply(new ReviewBoundaryApproved(id, Now, DomainId.New()));

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now));

        run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
        run.HumanGateTipSha.Should().BeNull();
    }

    [Fact]
    public void A_rebase_recovery_or_gate_repair_session_dispatched_after_an_answer_spends_it_because_an_agent_then_writes_commits()
    {
        foreach (bool recovery in new[] { true, false })
        {
            (RunAggregate run, Guid id) = Settled();
            ParkAtTheGate(run, id);
            run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, "ship it", Now, DomainId.New()));
            run.HumanGateAnswer.Should().Be(HumanGateAnswer.ResolvedMergeReady);

            if (recovery)
            {
                run.Apply(new PreFinalPassRebaseRecoveryDispatched(
                    id, DomainId.New(), 5003, Now, Now, AgentModel.Unknown, "abc1234", "def5678", "rebase-recovery"));
            }
            else
            {
                run.Apply(new SettlingGateRepairDispatched(
                    id, DomainId.New(), 5003, Now, Now, AgentModel.Unknown, "gate output", "gate-repair"));
            }

            run.HumanGateAnswer.Should().Be(HumanGateAnswer.None);
            run.HumanGateAnsweredAt.Should().BeNull();
            run.HumanGateTipSha.Should().BeNull();
        }
    }

    [Fact]
    public void A_session_dispatched_after_a_skip_leaves_the_skip_standing_because_it_was_the_owners_decision_to_open_as_it_comes_out()
    {
        (RunAggregate run, Guid id) = Settled();
        run.Apply(new ReviewSkippedToPullRequest(id, DomainId.New(), Now, "ship it", false, ["the final pass"]));

        run.Apply(new PreFinalPassRebaseRecoveryDispatched(
            id, DomainId.New(), 5003, Now, Now, AgentModel.Unknown, "abc1234", "def5678", "rebase-recovery"));

        run.HumanGateAnswer.Should().Be(HumanGateAnswer.Skipped);
    }
}
