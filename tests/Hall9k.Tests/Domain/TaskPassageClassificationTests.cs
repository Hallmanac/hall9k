using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The classification fold in <see cref="TaskPassageQuery"/> (idea 6e6d0200, Card 6): which phases of
/// a task a human ran, a delegated agent ran, the platform ran on its own, or an override skipped.
/// Pure, over <see cref="FakeEvent{T}"/> lists, so every claim here is a fact about the event
/// shapes the real commands record and not about a database.
/// </summary>
public sealed class TaskPassageClassificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TaskId = DomainId.New();
    private static readonly Guid Owner = DomainId.New();
    private const string ShortId = "5537e708";

    private static IEvent Ev<T>(T data) where T : notnull => new FakeEvent<T>(data);

    private static DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);

    /// <summary>One run's events, built up by the phase it records, so a test reads like the run.</summary>
    private sealed class RunStream(Guid runId)
    {
        public Guid RunId { get; } = runId;
        public List<IEvent> Events { get; } = [];

        public RunStream Dispatched(
            bool sentinel, string sessionName, bool headlessStart = false, bool followUp = false,
            ResumedReviewSettlement? resumed = null)
        {
            Events.Add(Ev(new RunDispatched(
                RunId, TaskId, sentinel ? Guid.Empty : DomainId.New(), Owner, 1, DomainId.New(), "/wt", "task/x",
                ExecutorMode.Subscription, At(0), IsFollowUp: followUp, SessionName: sessionName,
                IsDeliberateHeadlessStart: headlessStart, ResumedReviewSettlement: resumed)));
            return this;
        }

        public RunStream Verified()
        {
            Events.Add(Ev(new VerificationPassed(RunId, At(5))));
            return this;
        }

        public RunStream VerifyFailed()
        {
            Events.Add(Ev(new VerificationFailed(RunId, ["test"], At(5))));
            return this;
        }

        public RunStream Delegated()
        {
            Events.Add(Ev(new RunPhaseDelegated(
                RunId, "note", At(2), Owner, $"{ShortId}-build", "key", AgentModel.Fable)));
            return this;
        }

        public RunStream SessionCompleted()
        {
            Events.Add(Ev(new AgentSessionCompleted(RunId, At(10))));
            return this;
        }

        public RunStream ReviewCycle(int cycle)
        {
            Events.Add(Ev(new ReviewDispatched(RunId, DomainId.New(), cycle, 1, At(20), At(20))));
            return this;
        }

        public RunStream HumanFix(int cycle)
        {
            Events.Add(Ev(new ReviewHumanFixApplied(RunId, cycle, "sha", null, true, At(25), Owner)));
            return this;
        }

        public RunStream ParkResolved()
        {
            Events.Add(Ev(new ReviewParkResolved(RunId, ReviewVerdict.MergeReady, null, At(26), Owner)));
            return this;
        }

        public RunStream Skipped(params string[] skipped)
        {
            Events.Add(Ev(new ReviewSkippedToPullRequest(RunId, Owner, At(30), "ship it", false, skipped)));
            return this;
        }

        public RunStream PullRequestOpened()
        {
            Events.Add(Ev(new PullRequestOpened(RunId, "https://example.test/pr/1", 1, At(40))));
            return this;
        }

        public RunEventSet Set() => new(RunId, At(0), null, Events);
    }

    private static RunStream Run() => new(DomainId.New());

    private static IEvent Claimed(RunStream run, string? fingerprint = null, Guid? owner = null) =>
        Ev(new TaskClaimed(TaskId, Guid.Empty, owner ?? Owner, 1, run.RunId, At(0), OwnerRootFingerprint: fingerprint));

    private static IEvent Reopened(bool automatic) => Ev(new TaskReopened(
        TaskId, DomainId.New(), "task/x", "reason", At(50), Owner, FollowUpKind.ReviewFeedback, Automatic: automatic));

    private static TaskPassage Compute(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunEventSet> runs, TaskType? type = null) =>
        TaskPassageQuery.Compute(taskEvents, runs, type ?? TaskType.Feature, taskConcluded: false, Now);

    private static List<string> Shape(TaskPassage passage) =>
        [.. passage.Classification!.Phases.Select(phase => $"{phase.Kind}:{phase.Actor}")];

    // ---- the four classes ----

    [Fact]
    public void A_plain_daemon_run_is_automatic_in_every_phase()
    {
        RunStream run = Run().Dispatched(sentinel: false, $"{ShortId}-build").Verified().ReviewCycle(1).ReviewCycle(2)
            .PullRequestOpened();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal(
            "Build:Automatic", "Gates:Automatic", "ReviewCycle:Automatic", "ReviewCycle:Automatic",
            "PullRequestOpen:Automatic");
    }

    [Fact]
    public void A_hand_driven_build_with_its_hand_run_gates_is_human_and_the_daemons_laps_after_deliver_are_automatic()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").Verified().SessionCompleted()
            .Verified().ReviewCycle(1).PullRequestOpened();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal(
            "Build:Human", "Gates:Human", "Gates:Automatic", "ReviewCycle:Automatic", "PullRequestOpen:Automatic");
    }

    [Fact]
    public void A_delegated_build_under_a_claim_is_delegated_with_its_hand_run_gates()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").Delegated().Verified().SessionCompleted()
            .Verified().ReviewCycle(1);

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal("Build:Delegated", "Gates:Delegated", "Gates:Automatic", "ReviewCycle:Automatic");
    }

    [Fact]
    public void A_delegation_recorded_after_the_session_completed_does_not_make_the_build_delegated()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").SessionCompleted().Delegated();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal("Build:Human");
    }

    [Fact]
    public void A_pull_request_opened_under_the_override_is_human_and_each_phase_it_lists_as_skipped_is_skipped()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").Skipped(
            ReviewSkipPlan.Gates, "the adversarial review track", "the conformance review track",
            ReviewSkipPlan.FinalPass, ReviewSkipPlan.SettlingGate).PullRequestOpened();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal(
            "Build:Human", "Gates:Skipped", "ReviewCycle:Skipped", "ReviewCycle:Skipped", "PullRequestOpen:Human");
        passage.Classification!.Phases[^1].UnderSkipOverride.Should().BeTrue();
    }

    [Fact]
    public void A_skip_that_lists_no_gates_leaves_the_gates_already_run_as_they_were()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").Verified().SessionCompleted()
            .Skipped(ReviewSkipPlan.SettlingGate).PullRequestOpened();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal("Build:Human", "Gates:Human", "PullRequestOpen:Human");
    }

    [Fact]
    public void A_resumed_pull_request_open_keeps_the_override_through_its_settlement()
    {
        ResumedReviewSettlement overridden = new(ReviewSettlement.Overridden, 0, 0, 0, 0, [], 0, [], 0, 0, 0, 0);
        RunStream run = Run().Dispatched(sentinel: false, $"{ShortId}-build", resumed: overridden).PullRequestOpened();

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal("Build:Automatic", "PullRequestOpen:Human");
    }

    // ---- the claim key ----

    [Fact]
    public void An_h9k_task_start_run_is_automatic_and_so_is_a_task_verify_it_ran_itself()
    {
        RunStream run = Run().Dispatched(sentinel: true, $"{ShortId}-build", headlessStart: true).Verified()
            .SessionCompleted().ReviewCycle(1);

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        Shape(passage).Should().Equal("Build:Automatic", "Gates:Automatic", "ReviewCycle:Automatic");
    }

    [Fact]
    public void A_start_run_from_before_the_headless_flag_is_told_apart_by_its_build_session_name()
    {
        RunStream run = Run().Dispatched(sentinel: true, $"{ShortId}-build", headlessStart: false).Verified();

        Shape(Compute([Claimed(run)], [run.Set()])).Should().Equal("Build:Automatic", "Gates:Automatic");
    }

    [Theory]
    [InlineData("interactive-claim")]
    [InlineData("5537e708-interactive-claim")]
    [InlineData("")]
    public void An_h9k_task_work_claim_is_human(string sessionName)
    {
        RunStream run = Run().Dispatched(sentinel: true, sessionName);

        Shape(Compute([Claimed(run)], [run.Set()])).Should().Equal("Build:Human");
    }

    [Theory]
    [InlineData("5537e708-review-adversarial-1", true)]
    [InlineData("review-lap", false)]
    public void A_pr_review_task_under_the_sentinel_gets_no_classification(string sessionName, bool headless)
    {
        RunStream run = Run().Dispatched(sentinel: true, sessionName, headlessStart: headless);

        TaskPassage passage = Compute([Claimed(run)], [run.Set()], TaskType.PrReview);

        passage.Classification.Should().BeNull();
    }

    [Fact]
    public void A_task_with_no_human_held_run_reads_as_automatic_throughout()
    {
        RunStream first = Run().Dispatched(sentinel: false, $"{ShortId}-build").Verified().ReviewCycle(1).PullRequestOpened();
        RunStream lap = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true).Verified().ReviewCycle(1);

        TaskPassage passage = Compute([Claimed(first), Reopened(automatic: true), Claimed(lap)], [first.Set(), lap.Set()]);

        passage.Classification!.Phases.Should().OnlyContain(phase => phase.Actor == PhaseActor.Automatic);
    }

    // ---- closeout laps and the hand-off points ----

    [Fact]
    public void A_pull_request_stage_drive_lap_is_a_human_closeout_lap_and_the_daemons_laps_after_deliver_are_automatic()
    {
        RunStream build = Run().Dispatched(sentinel: false, $"{ShortId}-build").PullRequestOpened();
        RunStream drive = Run().Dispatched(sentinel: true, "interactive-claim", followUp: true).Verified().SessionCompleted()
            .Verified().ReviewCycle(1);

        TaskPassage passage = Compute(
            [Claimed(build), Reopened(automatic: false), Claimed(drive)], [build.Set(), drive.Set()]);

        Shape(passage).Should().Equal(
            "Build:Automatic", "PullRequestOpen:Automatic", "CloseoutLap:Human", "Gates:Human", "Gates:Automatic",
            "ReviewCycle:Automatic");
        passage.Classification!.Phases.Should().OnlyContain(phase => phase.HandOff == null);
    }

    [Fact]
    public void The_lap_after_a_handback_reads_as_automatic_and_names_the_hand_off()
    {
        RunStream drive = Run().Dispatched(sentinel: true, "interactive-claim", followUp: true).SessionCompleted();
        RunStream lap = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);

        TaskPassage passage = Compute(
            [Claimed(drive), Ev(new TaskHandedBack(TaskId, drive.RunId, "task/x", null, At(50), Owner)), Claimed(lap)],
            [drive.Set(), lap.Set()]);

        passage.Classification!.Phases[1].Actor.Should().Be(PhaseActor.Automatic);
        passage.Classification.Phases[1].HandOff.Should().Be(PhaseHandOff.Handback);
    }

    [Fact]
    public void The_lap_after_the_owners_go_on_pr_resolve_reads_as_automatic_and_names_the_hand_off()
    {
        RunStream build = Run().Dispatched(sentinel: false, $"{ShortId}-build").PullRequestOpened();
        RunStream lap = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);

        TaskPassage passage = Compute(
            [Claimed(build), Reopened(automatic: false), Claimed(lap)], [build.Set(), lap.Set()]);

        ClassifiedPhase closeout = passage.Classification!.Phases[^1];
        closeout.Kind.Should().Be(PhaseKind.CloseoutLap);
        closeout.Actor.Should().Be(PhaseActor.Automatic);
        closeout.HandOff.Should().Be(PhaseHandOff.PullRequestResolve);
    }

    [Fact]
    public void A_lap_closeout_reopened_on_its_own_names_no_hand_off()
    {
        RunStream build = Run().Dispatched(sentinel: false, $"{ShortId}-build").PullRequestOpened();
        RunStream lap = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);

        TaskPassage passage = Compute(
            [Claimed(build), Reopened(automatic: true), Claimed(lap)], [build.Set(), lap.Set()]);

        passage.Classification!.Phases[^1].HandOff.Should().BeNull();
    }

    // ---- human acts inside a phase ----

    [Fact]
    public void A_review_cycle_fixed_by_hand_stays_automatic_and_the_row_names_the_hand_fix()
    {
        RunStream run = Run().Dispatched(sentinel: false, $"{ShortId}-build").ReviewCycle(1).ReviewCycle(2).HumanFix(2);

        TaskPassage passage = Compute([Claimed(run)], [run.Set()]);

        passage.Classification!.Phases.Where(phase => phase.Kind == PhaseKind.ReviewCycle)
            .Select(phase => (phase.Actor, phase.FixedByHand))
            .Should().Equal((PhaseActor.Automatic, false), (PhaseActor.Automatic, true));
        passage.Classification.Describe().Should().Be("Build and review cycles 1 and 2 automatic, cycle 2 fixed by hand");
    }

    [Fact]
    public void A_single_review_cycle_fixed_by_hand_reads_as_automatic_fixed_by_hand()
    {
        RunStream run = Run().Dispatched(sentinel: false, $"{ShortId}-build").ReviewCycle(1).HumanFix(1);

        Compute([Claimed(run)], [run.Set()]).Classification!.Describe()
            .Should().Be("Build and review cycle 1 automatic, fixed by hand");
    }

    [Fact]
    public void A_park_resolution_or_boundary_approval_changes_no_class()
    {
        RunStream run = Run().Dispatched(sentinel: false, $"{ShortId}-build").ReviewCycle(1).ParkResolved();

        Shape(Compute([Claimed(run)], [run.Set()])).Should().Equal("Build:Automatic", "ReviewCycle:Automatic");
    }

    // ---- the row ----

    [Fact]
    public void The_row_groups_consecutive_phases_that_share_a_class()
    {
        RunStream build = Run().Dispatched(sentinel: true, "interactive-claim").Verified().SessionCompleted()
            .ReviewCycle(1).ReviewCycle(2).Skipped(ReviewSkipPlan.SettlingGate).PullRequestOpened();
        RunStream lap = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);

        TaskPassage passage = Compute(
            [Claimed(build), Reopened(automatic: false), Claimed(lap)], [build.Set(), lap.Set()]);

        passage.Classification!.Describe().Should().Be(
            "Build and gates by hand; review cycles 1 and 2 automatic; PR opened by hand (skip to PR); "
            + "closeout automatic after pr resolve");
    }

    [Fact]
    public void The_row_for_skipped_phases_and_a_delegated_build_reads_in_plain_words()
    {
        RunStream run = Run().Dispatched(sentinel: true, "interactive-claim").Delegated().Skipped(
            ReviewSkipPlan.Gates, "the adversarial review track", ReviewSkipPlan.SettlingGate).PullRequestOpened();

        Compute([Claimed(run)], [run.Set()]).Classification!.Describe().Should().Be(
            "Build delegated; gates and review skipped; PR opened by hand (skip to PR)");
    }

    [Fact]
    public void The_row_numbers_closeout_laps_only_when_there_is_more_than_one()
    {
        RunStream build = Run().Dispatched(sentinel: false, $"{ShortId}-build").PullRequestOpened();
        RunStream lapOne = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);
        RunStream lapTwo = Run().Dispatched(sentinel: false, $"{ShortId}-build", followUp: true);

        Compute([Claimed(build), Claimed(lapOne), Claimed(lapTwo)], [build.Set(), lapOne.Set(), lapTwo.Set()])
            .Classification!.Describe().Should().Be("Build, PR opened and closeout laps 1 and 2 automatic");
        Compute([Claimed(build), Claimed(lapOne)], [build.Set(), lapOne.Set()])
            .Classification!.Describe().Should().Be("Build, PR opened and closeout automatic");
    }

    [Fact]
    public void A_task_with_nothing_to_classify_has_no_row()
    {
        Compute([], []).Classification!.Describe().Should().BeNull();
    }

    // ---- old streams ----

    [Fact]
    public void A_stream_with_none_of_the_override_report_or_pull_request_stage_events_classifies_by_the_run_rules()
    {
        // Only the events that existed before Cards 3 to 5: dispatch, gates, review, open, a claim
        // with no recorded owner fingerprint, and a start run from before the headless flag.
        RunStream legacyStart = Run().Dispatched(sentinel: true, $"{ShortId}-build").Verified().ReviewCycle(1).PullRequestOpened();
        RunStream legacyWork = Run().Dispatched(sentinel: true, string.Empty).Verified();

        Shape(Compute([Claimed(legacyStart, fingerprint: null)], [legacyStart.Set()])).Should().Equal(
            "Build:Automatic", "Gates:Automatic", "ReviewCycle:Automatic", "PullRequestOpen:Automatic");
        Shape(Compute([Claimed(legacyWork)], [legacyWork.Set()])).Should().Equal("Build:Human", "Gates:Human");
    }

    [Fact]
    public void A_phase_is_keyed_to_the_claim_it_ran_under()
    {
        Guid second = DomainId.New();
        RunStream build = Run().Dispatched(sentinel: false, $"{ShortId}-build");
        RunStream lap = Run().Dispatched(sentinel: true, "interactive-claim", followUp: true);

        TaskPassage passage = Compute(
            [Claimed(build, fingerprint: "SHA256:alice"), Claimed(lap, owner: second)], [build.Set(), lap.Set()]);

        passage.Classification!.Phases[0].Owner.Should().Be(new ClaimOwner(Owner, "SHA256:alice"));
        passage.Classification.Phases[1].Owner.Should().Be(new ClaimOwner(second, null));
    }
}
