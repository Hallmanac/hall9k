using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class RunAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_happy_path_walks_dispatched_to_awaiting_review()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();

        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), LeaseGeneration: 1,
            SessionId: DomainId.New(), WorktreePath: "/wt/x", Branch: "task/x",
            ExecutorMode.Subscription, Now));
        run.State.Should().Be(RunState.Dispatched);
        run.ExecutorMode.UsesBareFlag.Should().BeFalse("subscription mode never uses --bare (log #1)");

        run.Apply(new RunProcessStarted(id, ProcessId: 4482, Now));
        run.State.Should().Be(RunState.Running);

        run.Apply(new AgentSessionCompleted(id, Now));
        run.State.Should().Be(RunState.Verifying);

        run.Apply(new TokensRecorded(id, InputTokens: 120_000, OutputTokens: 30_000, CostUsd: null, Now));
        run.InputTokens.Should().Be(120_000);

        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new PullRequestOpened(id, "https://github.com/x/y/pull/7", 7, Now));
        run.State.Should().Be(RunState.AwaitingReview);
        run.State.IsLive.Should().BeFalse();

        run.Apply(new RunCompleted(id, Now));
        run.State.Should().Be(RunState.Completed);
        run.State.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Follow_up_run_reaches_awaiting_review_through_pull_request_updated()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();

        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), LeaseGeneration: 2,
            SessionId: DomainId.New(), WorktreePath: "/wt/x", Branch: "task/x",
            ExecutorMode.Subscription, Now));
        run.Apply(new RunProcessStarted(id, ProcessId: 4483, Now));
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new VerificationPassed(id, Now));

        run.Apply(new PullRequestUpdated(id, "https://github.com/x/y/pull/7", 7, Now));

        run.State.Should().Be(RunState.AwaitingReview, "a follow-up updates the existing PR instead of opening one");
        run.PullRequestUrl.Should().Be("https://github.com/x/y/pull/7");
        run.PullRequestNumber.Should().Be(7);
    }

    [Fact]
    public void Review_loop_walks_dispatch_verdict_fix_and_reverify_with_tokens_accumulating()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new RunProcessStarted(id, 4484, Now));
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new TokensRecorded(id, 100_000, 20_000, null, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.None, "no review has been dispatched yet");

        Guid conformanceSession = DomainId.New();
        Guid adversarialSession = DomainId.New();
        run.Apply(new ReviewDispatched(
            id, conformanceSession, Cycle: 1, ProcessId: 5001, Now, Now, Lens: ReviewLens.Conformance));
        run.Apply(new ReviewDispatched(
            id, adversarialSession, Cycle: 1, ProcessId: 5011, Now, Now, Lens: ReviewLens.Adversarial));
        run.State.Should().Be(RunState.UnderReview);
        run.State.IsLive.Should().BeTrue("a review session is a live process the daemon watches");
        run.ReviewPhase.Should().Be(ReviewPhase.AwaitingVerdict);
        run.InFlightReviewPasses.Select(pass => pass.SessionId).Should().Equal(
            [conformanceSession, adversarialSession], "both lenses are in flight, in dispatch order");

        run.Apply(new TokensRecorded(id, 30_000, 5_000, null, Now));
        run.Apply(new ReviewPassCompleted(id, 1, ReviewLens.Conformance, ReviewVerdict.MergeReady, Now));
        run.ReviewPhase.Should().Be(
            ReviewPhase.AwaitingVerdict, "one clean lens is not a cycle: the other is still reading");
        run.Apply(new ReviewPassCompleted(id, 1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now));
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded, "either track finding real problems needs fixes");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        run.InFlightReviewPasses.Should().BeEmpty("the verdicts retire both review sessions");
        run.CompletedReviewPasses.Select(pass => pass.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial], "the cycle records which lens said what");
        run.ActiveReviewLenses.Should().Equal(
            [ReviewLens.Adversarial], "the clean track went dormant and is not dispatched again");

        Guid fixSession = DomainId.New();
        run.Apply(new ReviewFixDispatched(id, fixSession, Cycle: 1, ProcessId: 5002, Now, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.AwaitingFix);
        run.ReviewFixRuns.Should().Be(1, "one fix session per cycle, whichever lenses found something");
        run.ActiveFixSessionId.Should().Be(fixSession);

        run.Apply(new TokensRecorded(id, 40_000, 8_000, null, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.Reverify, "a fixed outcome re-runs the gates before the next review");

        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(
            id, DomainId.New(), Cycle: 2, ProcessId: 5013, Now, Now, Lens: ReviewLens.Adversarial));
        run.CompletedReviewPasses.Should().BeEmpty("a new cycle starts with no answers, only the last cycle's history");
        run.Apply(new TokensRecorded(id, 25_000, 4_000, null, Now));
        run.Apply(new ReviewPassCompleted(id, 2, ReviewLens.Adversarial, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewCompleted(id, 2, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Adversarial, 2, ReviewSettlement.Clean, [], Now));
        run.ReviewPhase.Should().Be(
            ReviewPhase.Settling, "every track has concluded; what is left is recording how the loop ended");
        run.DeriveSettlement().Should().Be(ReviewSettlement.Clean, "no track left a residual behind");

        run.Apply(new ReviewSettled(id, 2, ReviewSettlement.Clean, 0, 0, 0, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady, "merge-ready takes EVERY track concluded");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Clean);
        run.ReviewCycle.Should().Be(2);

        run.InputTokens.Should().Be(195_000, "review and fix sessions record tokens like any other session");
        run.OutputTokens.Should().Be(37_000);

        run.Apply(new PullRequestOpened(id, "https://github.com/x/y/pull/8", 8, Now));
        run.State.Should().Be(RunState.AwaitingReview);
    }

    /// <summary>
    /// The loop's designed exit: the severity gate lets a medium through, every track concludes,
    /// and one last fix session runs over findings no reviewer will read again. What ships
    /// unreviewed there is the reviewers' reading of those commits, never the gates — so the fix
    /// completion goes to Reverify like every other one, and the reverify step settles.
    /// </summary>
    [Fact]
    public void The_terminal_fix_still_re_runs_the_gates_before_the_loop_settles()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewDispatched(
            id, DomainId.New(), Cycle: 4, ProcessId: 5001, Now, Now, Lens: ReviewLens.Adversarial));
        run.Apply(new ReviewPassCompleted(
            id, 4, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now,
            [new ReviewFindingRecord(
                ReviewSeverity.Medium, ReviewFindingScope.InScope, "Auth.cs:42", ReviewFindingDisposition.Fix)]));
        run.Apply(new ReviewCompleted(id, 4, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now));
        run.Apply(new ReviewTrackConcluded(
            id, ReviewLens.Adversarial, 4, ReviewSettlement.Settled,
            [new ReviewResidual(
                ReviewLens.Adversarial, 4, ReviewSeverity.Medium, ReviewFindingScope.InScope,
                ReviewResidualDisposition.FixedUnreviewed, "Auth.cs:42")],
            Now));

        run.ActiveReviewLenses.Should().BeEmpty("the gate concluded the last live track");
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded, "the medium is still fixed, just never re-reviewed");

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), Cycle: 4, ProcessId: 5002, Now, Now));
        run.Apply(new ReviewFixCompleted(id, 4, ReviewFixOutcome.Fixed, Now));
        run.ReviewPhase.Should().Be(
            ReviewPhase.Reverify,
            "the terminal fix's commits reach the pull request, so the build and test gates run over them");

        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewSettled(id, 4, run.DeriveSettlement(), 1, 0, 0, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled);
    }

    /// <summary>
    /// A finding routed away in a cycle the track goes on running past is still a finding this
    /// pull request exported to a draft bug task, so it is a residual from the moment it is
    /// routed. Recording it only when the track concludes would let the run settle Clean over a
    /// defect it knowingly shipped — the one reading a settled ending exists to prevent.
    /// </summary>
    [Fact]
    public void A_finding_routed_in_a_continuing_cycle_is_a_residual_from_the_moment_it_is_routed()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewDispatched(
            id, DomainId.New(), Cycle: 1, ProcessId: 5001, Now, Now, Lens: ReviewLens.Adversarial));
        run.Apply(new ReviewPassCompleted(
            id, 1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now,
            [
                new ReviewFindingRecord(
                    ReviewSeverity.High, ReviewFindingScope.InScope, "Spawner.cs:60", ReviewFindingDisposition.Fix),
                new ReviewFindingRecord(
                    ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Legacy.cs:12",
                    ReviewFindingDisposition.Route),
            ]));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        // The high forces cycle two, so the adversarial track writes no conclusion here — and
        // the medium leaves for its draft anyway.
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 1, ReviewSeverity.Medium, "Legacy.cs:12", DomainId.New(), null, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now));

        run.ReviewResiduals.Should().ContainSingle().Which.Should().BeEquivalentTo(new ReviewResidual(
            ReviewLens.Adversarial, 1, ReviewSeverity.Medium, ReviewFindingScope.OutOfScope,
            ReviewResidualDisposition.Routed, "Legacy.cs:12"));

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), Cycle: 1, ProcessId: 5002, Now, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(
            id, DomainId.New(), Cycle: 2, ProcessId: 5003, Now, Now, Lens: ReviewLens.Adversarial));
        run.Apply(new ReviewPassCompleted(id, 2, ReviewLens.Adversarial, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewCompleted(id, 2, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewTrackConcluded(id, ReviewLens.Adversarial, 2, ReviewSettlement.Clean, [], Now));

        run.ReviewPhase.Should().Be(ReviewPhase.Settling);
        run.DeriveSettlement().Should().Be(
            ReviewSettlement.Settled,
            "both tracks ended clean, but a known defect left this pull request for a draft bug task");
        run.ReviewResiduals.Count(residual => residual.Disposition == ReviewResidualDisposition.Routed)
            .Should().Be(1, "the routed finding is counted once, in the cycle it was routed in");
    }

    /// <summary>
    /// Routing that could not create its draft is a residual all the same — the finding still
    /// left this pull request — but it is not a routing. No draft bug task exists for it, so
    /// recording it as Routed would count and report a task nobody can open, and a human
    /// reading "1 routed" would believe the defect is written down where they can find it.
    /// </summary>
    [Fact]
    public void A_routing_that_failed_to_create_its_draft_is_a_residual_but_not_a_routing()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 3, ReviewSeverity.Low, "Legacy.cs:88", null, "the draft stream would not save", Now));

        run.ReviewResiduals.Should().ContainSingle()
            .Which.Disposition.Should().Be(ReviewResidualDisposition.RoutingFailed);
        run.ReviewResiduals.Should().NotContain(
            residual => residual.Disposition == ReviewResidualDisposition.Routed,
            "no draft was created, and the count a human reads must not claim one was");
        run.DeriveSettlement().Should().Be(ReviewSettlement.Settled);
    }

    /// <summary>
    /// A failed routing is deliberately retried on a later cycle, so one defect leaves two
    /// records: the attempt that wrote no draft, and the retry that did. The settlement reports
    /// defects rather than records, or it tells a human "1 routed, 1 not routed" about a single
    /// exported defect and sends them looking for one that lives nowhere but this stream.
    /// </summary>
    [Fact]
    public void A_routing_retried_after_a_failure_settles_as_one_routed_defect()
    {
        RunAggregate run = Dispatched(out Guid id);
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 2, ReviewSeverity.Medium, "Legacy.cs:40", null, "the store was unreachable", Now));
        // The next cycle's reviewer states the same place in its own hand, and the retry works.
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 3, ReviewSeverity.Medium, "./src/Legacy.cs:40", DomainId.New(), null, Now));

        ReviewResidualTally tally = run.DeriveResidualTally();

        tally.Routed.Should().Be(1, "one defect was exported, on the second attempt");
        tally.RoutingFailed.Should().Be(0,
            "the draft the first attempt could not write exists now, so nothing survives only in this stream");
        run.ReviewResiduals.Should().HaveCount(2, "both attempts happened, and the stream records what happened");
    }

    /// <summary>
    /// The same reading in the other direction: a place two cycles failed on is one defect
    /// nobody can open a draft for, not two.
    /// </summary>
    [Fact]
    public void A_routing_that_failed_twice_is_one_unrouted_defect()
    {
        RunAggregate run = Dispatched(out Guid id);
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 2, ReviewSeverity.Medium, "Legacy.cs:40", null, "the store was unreachable", Now));
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 3, ReviewSeverity.Medium, "Legacy.cs:40", null, "the store was unreachable", Now));
        // A finding the reviewer never placed cannot be shown to be either of those, so it
        // counts on its own rather than being folded into a defect it may have nothing to do with.
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 3, ReviewSeverity.Low, "", null, "the store was unreachable", Now));

        ReviewResidualTally tally = run.DeriveResidualTally();

        tally.Routed.Should().Be(0);
        tally.RoutingFailed.Should().Be(2, "one place that failed twice, and one unplaced finding of its own");
    }

    /// <summary>
    /// A place can be fixed unreviewed twice over by two roads: the tracks conclude separately,
    /// so both lenses can end on the same defect, and one terminal cycle can state that place in
    /// two finding blocks. Either way it is one defect shipped without a second read, and the
    /// count a human weighs the pull request by has to say one.
    /// </summary>
    [Fact]
    public void A_place_two_tracks_end_on_is_one_fixed_unreviewed_defect()
    {
        RunAggregate run = Dispatched(out Guid id);
        run.Apply(new ReviewTrackConcluded(
            id, ReviewLens.Conformance, 3, ReviewSettlement.Settled,
            [Unreviewed(ReviewLens.Conformance, "Auth.cs:42"),
             // The same cycle's reviewer wrote the one place up twice, in its own hand each time.
             Unreviewed(ReviewLens.Conformance, "./src/Auth.cs:42")],
            Now));
        run.Apply(new ReviewTrackConcluded(
            id, ReviewLens.Adversarial, 4, ReviewSettlement.Settled,
            [Unreviewed(ReviewLens.Adversarial, "Auth.cs:42"),
             // A finding the reviewer never placed stands on its own, as it does for routing.
             Unreviewed(ReviewLens.Adversarial, "")],
            Now));

        ReviewResidualTally tally = run.DeriveResidualTally();

        tally.FixedUnreviewed.Should().Be(2, "one place both tracks ended on, and one unplaced finding");
        run.ReviewResiduals.Should().HaveCount(4, "every record stays, because every one of them happened");
    }

    /// <summary>
    /// The counts collapse within themselves and never against each other. A defect one track
    /// fixed unreviewed and another exported to a draft bug task really did meet both ends, and
    /// a human deciding how far to trust the pull request is owed both readings of it.
    /// </summary>
    [Fact]
    public void A_defect_fixed_on_one_track_and_routed_on_the_other_is_counted_by_both()
    {
        RunAggregate run = Dispatched(out Guid id);
        run.Apply(new ReviewFindingRouted(
            id, ReviewLens.Adversarial, 2, ReviewSeverity.Low, "Legacy.cs:40", DomainId.New(), null, Now));
        run.Apply(new ReviewTrackConcluded(
            id, ReviewLens.Conformance, 3, ReviewSettlement.Settled,
            [Unreviewed(ReviewLens.Conformance, "Legacy.cs:40")],
            Now));

        ReviewResidualTally tally = run.DeriveResidualTally();

        tally.FixedUnreviewed.Should().Be(1);
        tally.Routed.Should().Be(1, "the export happened, whatever the other track did to the same place");
    }

    private static ReviewResidual Unreviewed(ReviewLens lens, string location) =>
        new(lens, 3, ReviewSeverity.Medium, ReviewFindingScope.InScope,
            ReviewResidualDisposition.FixedUnreviewed, location);

    private static RunAggregate Dispatched(out Guid id)
    {
        RunAggregate run = new();
        id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        return run;
    }

    [Fact]
    public void Disputed_fix_and_review_park_take_the_run_to_needs_human_territory()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Disputed, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.Disputed, "a dispute never loops — a park follows");

        run.Apply(new ReviewParked(id, "The fix run disputed a review finding.", Now));
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewPhase.Should().Be(ReviewPhase.Parked);
        run.State.IsLive.Should().BeFalse("parked means waiting on a human, not a live process");
        run.State.IsTerminal.Should().BeFalse("a parked run is a waiting state, not a failure");
    }

    [Fact]
    public void Verdict_less_review_walks_one_reprompt_then_the_park()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));

        Guid conformanceSession = DomainId.New();
        Guid adversarialSession = DomainId.New();
        run.Apply(new ReviewDispatched(
            id, conformanceSession, Cycle: 1, ProcessId: 5001, Now, Now, Lens: ReviewLens.Conformance));
        run.Apply(new ReviewDispatched(
            id, adversarialSession, Cycle: 1, ProcessId: 5011, Now, Now, Lens: ReviewLens.Adversarial));
        run.Apply(new ReviewPassCompleted(id, 1, ReviewLens.Conformance, ReviewVerdict.Unknown, Now));
        run.Apply(new ReviewPassCompleted(id, 1, ReviewLens.Adversarial, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.Unknown, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.VerdictMissing, "no parseable verdict means a re-prompt, not a guess");
        run.CompletedReviewPasses.Single(pass => pass.Verdict == ReviewVerdict.Unknown).SessionId.Should().Be(
            conformanceSession, "the re-prompt resumes the pass that already read the diff");
        run.VerdictRepromptedCycle.Should().Be(0, "the one re-prompt is still available");

        Guid artifactId = DomainId.New();
        run.Apply(new ReviewVerdictReprompted(
            id, artifactId, conformanceSession, Cycle: 1, ProcessId: 5002, Now, Now, Lens: ReviewLens.Conformance));
        run.ReviewPhase.Should().Be(ReviewPhase.AwaitingVerdict);
        run.InFlightReviewPasses.Single().SessionId.Should().Be(
            artifactId, "the resumed leg gets its own artifact identity");
        run.InFlightReviewPasses.Single().TranscriptSessionId.Should().Be(
            conformanceSession, "the transcript still belongs to the original session");
        run.VerdictRepromptedCycle.Should().Be(1, "the cycle's one re-prompt is now spent — the CYCLE's, not each lens's");

        run.Apply(new ReviewPassCompleted(id, 1, ReviewLens.Conformance, ReviewVerdict.Unknown, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.Unknown, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.VerdictMissing, "still verdict-less — and the re-prompt is spent");
        run.CompletedReviewPasses.Should().HaveCount(2, "a re-prompted pass replaces its own answer, it does not add one");

        run.Apply(new ReviewParked(id, "No parseable verdict, even after a re-prompt.", Now));
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewPhase.Should().Be(ReviewPhase.Parked);
    }

    [Fact]
    public void Review_park_resolved_merge_ready_puts_the_run_back_on_the_road_to_its_pull_request()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.Unknown, Now));
        run.Apply(new ReviewParked(id, "No parseable verdict.", Now));

        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, null, Now, DomainId.New()));

        run.State.Should().Be(RunState.UnderReview, "the daemon's resume sweep drives it from here");
        run.ReviewPhase.Should().Be(
            ReviewPhase.Settling, "the loop still owes the stream an account of how it ended");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.DeriveSettlement().Should().Be(
            ReviewSettlement.Settled, "a human ending the loop is not a reviewer reading the final tip");

        run.Apply(new ReviewSettled(id, 1, ReviewSettlement.Settled, 0, 0, 0, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled);
    }

    /// <summary>
    /// The thread-dispute park (Decisions Log #62) is raised from Verifying, before a gate has
    /// run and before any reviewer has read the diff, so a merge-ready resolution there is the
    /// human settling the disputed THREAD. It must not report merge-ready to PullRequestOpener,
    /// which would force-push commits that never compiled here: the pipeline re-enters at the
    /// gates, and a review cycle follows them.
    /// </summary>
    [Fact]
    public void Merge_ready_on_a_thread_dispute_park_re_enters_at_the_gates_rather_than_the_pull_request()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now, IsFollowUp: true));
        run.Apply(new RunProcessStarted(id, 5001, Now));
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new ReviewParked(id, "A follow-up disputed a review thread.", Now));
        run.ParkedFromState.Should().Be(RunState.Verifying, "the park caught the run before the gates");

        run.Apply(new ReviewParkResolved(id, ReviewVerdict.MergeReady, null, Now, DomainId.New()));

        run.State.Should().Be(RunState.UnderReview, "the resume sweep drives it from here either way");
        run.ReviewPhase.Should().Be(ReviewPhase.Reverify, "gates first, then a review cycle");
        run.LastReviewVerdict.Should().Be(
            ReviewVerdict.Unknown, "no reviewer read this diff — recording a verdict would invent one");
    }

    [Fact]
    public void Review_park_resolved_needs_fixes_regrants_the_cycle_caps_and_carries_the_human_findings()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now));
        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), 2, 5003, Now, Now));
        run.Apply(new ReviewCompleted(id, 2, ReviewVerdict.NeedsFixes, Now));
        run.Apply(new ReviewParked(id, "The conformance track reached its cap.", Now));
        run.ReviewFixRuns.Should().Be(1);
        run.ReviewBudgetBaseCycle.Should().Be(0, "nothing has been re-granted yet");

        run.Apply(new ReviewParkResolved(
            id, ReviewVerdict.NeedsFixes, "The limiter finding is real; fix it.", Now, DomainId.New()));

        run.State.Should().Be(RunState.UnderReview);
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded);
        run.ReviewBudgetBaseCycle.Should().Be(
            2, "the human asking is a fresh grant (log #22): the cycle caps are re-measured from here");
        run.ReviewFixRuns.Should().Be(
            1, "the fix count is a record of what ran, not a budget, so it is not rewritten");
        run.PendingHumanFindings.Should().Be("The limiter finding is real; fix it.");

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 2, 5004, Now, Now));
        run.PendingHumanFindings.Should().BeNull("the dispatch consumed the human findings");
        run.ReviewFixRuns.Should().Be(2, "the count keeps counting every fix session the run actually ran");
    }

    /// <summary>
    /// A budget park is not a human waypoint the way ReviewParked is (backlog 40): it is
    /// waiting on a clock, and the retry sweep resumes the same run — process id changes,
    /// identity does not — the moment the window is likely to have reset.
    /// </summary>
    [Fact]
    public void Budget_exhausted_run_parks_and_a_retry_resumes_it_live()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now));
        run.Apply(new RunProcessStarted(id, 4482, Now));

        run.Apply(new RunBudgetExhausted(id, "Claude AI usage limit reached|1762952400", Now));

        run.State.Should().Be(RunState.BudgetParked);
        run.State.IsLive.Should().BeFalse("nothing is running while the window is shut");
        run.State.IsTerminal.Should().BeFalse("the work is intact; this is a wait, not an ending");

        run.Apply(new RunResumed(id, ProcessId: 4999, Now));

        run.State.Should().Be(RunState.Running, "the retry sweep clears the hold with no human act");
    }

    [Fact]
    public void Superseded_run_is_terminal_with_the_superseding_generation_recorded()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.ApiKey, Now));
        run.ExecutorMode.UsesBareFlag.Should().BeTrue("api-key mode is where --bare lives (log #1)");

        run.Apply(new RunSuperseded(id, SupersededByGeneration: 2, Now));
        run.State.Should().Be(RunState.Superseded);
        run.State.IsTerminal.Should().BeTrue();
    }
}
