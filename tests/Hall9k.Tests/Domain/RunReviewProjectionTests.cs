using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The pre-PR review loop (Decisions Log #23) through the run read models: UnderReview
/// between Verifying and AwaitingReview, the verdict on the details row, and a park that
/// surfaces the reason without failing the run.
/// </summary>
public sealed class RunReviewProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Run_details_reads_under_review_with_the_cycle_and_verdict_recorded()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        view.State.Should().Be(RunState.UnderReview, "review sits between Verifying and AwaitingReview");
        view.ReviewCycle.Should().Be(1);

        projection.Apply(new FakeEvent<TokensRecorded>(new TokensRecorded(id, 30_000, 5_000, null, Now)), view);
        projection.Apply(new FakeEvent<ReviewCompleted>(
            new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now)), view);
        view.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        view.State.Should().Be(RunState.UnderReview, "the loop owns the run until merge-ready or a park");

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 2, 5002, Now, Now)), view);
        projection.Apply(new FakeEvent<ReviewCompleted>(
            new ReviewCompleted(id, 2, ReviewVerdict.MergeReady, Now)), view);
        view.ReviewCycle.Should().Be(2);
        view.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);

        projection.Apply(new FakeEvent<PullRequestOpened>(
            new PullRequestOpened(id, "https://github.com/x/y/pull/9", 9, Now)), view);
        view.State.Should().Be(RunState.AwaitingReview);
        view.InputTokens.Should().Be(30_000, "review sessions record tokens on the run");
    }

    /// <summary>
    /// Which shape a cycle's dispatch took is a stream fact `h9k task show` and the phase line
    /// read (task: review cycles after the first) — Discovery for a stream that never recorded a
    /// mode, and whichever word a later cycle actually recorded otherwise.
    /// </summary>
    [Fact]
    public void Run_details_reads_the_recorded_review_mode_and_defaults_to_discovery()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        view.ReviewCycleMode.Should().Be(
            ReviewMode.Discovery, "a stream that never recorded a mode reads as the shape every cycle had before this field existed");

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 2, 5002, Now, Now, null, ReviewLens.Verify, ReviewMode.Verify)), view);
        view.ReviewCycleMode.Should().Be(ReviewMode.Verify);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 3, 5003, Now, Now, null, ReviewLens.Conformance, ReviewMode.FinalFullPass)), view);
        view.ReviewCycleMode.Should().Be(ReviewMode.FinalFullPass);
    }

    [Fact]
    public void Run_details_parks_with_the_reason_and_stays_unfinished()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "Review still finds defects after 2 automatic fix run(s).", Now)), view);

        view.State.Should().Be(RunState.ReviewParked);
        view.ParkedReason.Should().Contain("2 automatic fix run(s)");
        view.FinishedAt.Should().BeNull("parked is a waiting state, not an ending");
        view.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Run_details_leaves_the_park_when_a_human_resolves_it()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "No parseable verdict, even after a re-prompt.", Now)), view);

        projection.Apply(new FakeEvent<ReviewParkResolved>(
            new ReviewParkResolved(id, ReviewVerdict.MergeReady, null, Now, DomainId.New())), view);

        view.State.Should().Be(RunState.UnderReview, "a resolved park no longer needs the human");
        view.ParkedReason.Should().BeNull("the reason is answered; h9k status must stop showing it");
        view.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady, "the human's verdict is the run's verdict now");
        view.ReviewParkResolutions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ReviewParkResolution(1, ReviewVerdict.MergeReady, null, Now),
            "the resolution is kept as history, not just folded into LastReviewVerdict, so a later " +
            "review pass can be told what a human already settled");
    }

    /// <summary>
    /// The takeover lever's own source of truth (independent pre-PR review, cycle 5, adversarial
    /// finding): a cap-0 takeover park or the lifetime-budget park records that granting
    /// --needs-fixes here buys no progress, so <c>h9k status</c> can agree with the park's own
    /// reason instead of offering a lever the run would just re-park on. An ordinary park leaves
    /// the flag false, and a resolved park clears it so a later, ordinary park never inherits it.
    /// </summary>
    [Fact]
    public void Run_details_records_when_needs_fixes_offers_no_progress_and_clears_it_on_resolve()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "The conformance review's cap is 0, from a task override.", Now, true)), view);

        view.ParkedNeedsFixesOffersNoProgress.Should().BeTrue();

        projection.Apply(new FakeEvent<ReviewParkResolved>(
            new ReviewParkResolved(id, ReviewVerdict.MergeReady, null, Now, DomainId.New())), view);

        view.ParkedNeedsFixesOffersNoProgress.Should().BeFalse(
            "the park is answered, so a later park must not inherit a stale claim about this one");
    }

    /// <summary>
    /// Merge-ready is one word for two different claims (Decisions Log #63), so the row carries
    /// the settlement beside the verdict: a settled ending reports the residuals it shipped
    /// without a second read, and a run whose review predates settlements reports neither
    /// rather than being read as clean.
    /// <para>
    /// The counts arrive with the terminal event rather than being tallied from each track's
    /// conclusion, which is what the assertions before it pin: the residuals a concluded track
    /// carries move nothing in this view on their own.
    /// </para>
    /// </summary>
    [Fact]
    public void Run_details_records_how_merge_ready_was_reached_and_what_it_left_behind()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 4, 5001, Now, Now, null, ReviewLens.Adversarial)), view);
        view.ReviewSettlement.Should().Be(ReviewSettlement.Unknown, "the loop is still running");
        view.ReviewResidualsFixed.Should().Be(0);
        view.ReviewResidualsRouted.Should().Be(0);

        projection.Apply(new FakeEvent<ReviewSettled>(
            new ReviewSettled(id, 4, ReviewSettlement.Settled, 1, 1, 0, Now)), view);
        view.LastReviewVerdict.Should().Be(
            ReviewVerdict.MergeReady, "the terminal verdict is MergeReady however the loop got here");
        view.ReviewSettlement.Should().Be(ReviewSettlement.Settled);
        view.ReviewResidualsFixed.Should().Be(1, "the terminal event states the count this view reports");
        view.ReviewResidualsRouted.Should().Be(1);
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, conformance finding: the count alone left a reader with
    /// no way to identify what actually rode along, so <see cref="ReviewSettled.RideAlongFindings"/>
    /// carries each one's severity and location onto this view too. A stream written before that
    /// field existed still reports the count with an empty list — an honest gap, not a guess.
    /// </summary>
    [Fact]
    public void Run_details_names_each_ride_along_finding_the_settle_event_carries()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewSettled>(
            new ReviewSettled(
                id, 4, ReviewSettlement.Settled, 0, 0, 0, Now, ResidualsRideAlong: 1,
                RideAlongFindings: [new ReviewRideAlongFinding(ReviewSeverity.Medium, "Auth.cs:9")])),
            view);

        view.ReviewResidualsRideAlong.Should().Be(1);
        view.ReviewRideAlongFindings.Should().ContainSingle()
            .Which.Should().Match<ReviewRideAlongFinding>(
                finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "Auth.cs:9");
    }

    /// <summary>
    /// A thread-dispute park (Decisions Log #62) lands from Verifying, before any review pass has
    /// read the diff — the human resolving it decided the disputed THREAD, not a review finding,
    /// so it must not ride into a later review prompt as though a fresh-context reviewer's own
    /// finding had already been settled (adversarial cycle-4 finding, `RunDetails.cs:344`): a
    /// reviewer instructed "the human already dismissed this" over a defect nobody has ever
    /// actually reviewed would suppress a real finding on no evidence at all.
    /// </summary>
    [Fact]
    public void Run_details_does_not_record_a_thread_dispute_park_resolution_as_a_settled_ruling()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now, IsFollowUp: true)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);

        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "A follow-up disputed a review thread.", Now)), view);

        projection.Apply(new FakeEvent<ReviewParkResolved>(new ReviewParkResolved(
            id, ReviewVerdict.MergeReady, "the thread is about the retry loop; I already checked it, leave it",
            Now, DomainId.New())), view);

        view.State.Should().Be(RunState.UnderReview);
        view.ReviewParkResolutions.Should().BeEmpty(
            "no review pass ever ran over this diff, so there is nothing settled to hand a later reviewer");
    }

    /// <summary>
    /// The same discriminator applies whichever verdict the human gives a dispute park: a
    /// needs-fixes resolution of a rebase-conflict dispute (backlog 44) is the human deciding
    /// which side of the conflict to take, not confirming a review finding as real.
    /// </summary>
    [Fact]
    public void Run_details_does_not_record_a_needs_fixes_thread_dispute_resolution_either()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now, IsFollowUp: true)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);

        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "A rebase follow-up hit an unresolvable conflict.", Now)), view);

        projection.Apply(new FakeEvent<ReviewParkResolved>(new ReviewParkResolved(
            id, ReviewVerdict.NeedsFixes, "take theirs for the ReviewEngine.cs hunk", Now, DomainId.New())), view);

        view.ReviewParkResolutions.Should().BeEmpty(
            "resolving a rebase dispute is not a human confirming a review finding as real");
    }

    /// <summary>
    /// A resumed dispute that disputes again reaches its second park from UnderReview, not
    /// Verifying (<see cref="ReviewFixDispatched"/> moves <c>State</c> there before the resumed
    /// session ever parks again), so keying the exclusion on State-at-park-time would misread
    /// the re-dispute's resolution as a settled review ruling (cycle-6 human triage). ReviewCycle
    /// stays 0 for the whole round trip regardless, which is what the fix keys on instead.
    /// </summary>
    [Fact]
    public void Run_details_does_not_record_a_second_thread_dispute_resolution_as_a_settled_ruling()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now, IsFollowUp: true)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);

        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "A follow-up disputed a review thread.", Now)), view);
        projection.Apply(new FakeEvent<ReviewParkResolved>(new ReviewParkResolved(
            id, ReviewVerdict.NeedsFixes, "the thread is real; take their side", Now, DomainId.New())), view);

        // The resumed fix session over the dispute — DispatchFixSessionAsync's cycle-0 resume —
        // moves State to UnderReview before the resumed session ever parks again.
        projection.Apply(new FakeEvent<ReviewFixDispatched>(
            new ReviewFixDispatched(id, DomainId.New(), 0, 4485, Now, Now)), view);

        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "The resumed follow-up still disputed the thread.", Now)), view);

        projection.Apply(new FakeEvent<ReviewParkResolved>(new ReviewParkResolved(
            id, ReviewVerdict.MergeReady, "checked again; still not a defect", Now, DomainId.New())), view);

        view.ReviewCycle.Should().Be(0, "no ordinary review pass ever ran over this diff");
        view.ReviewParkResolutions.Should().BeEmpty(
            "the re-dispute's resolution is still deciding the disputed thread, not a review finding, " +
            "even though State was UnderReview rather than Verifying the second time it parked");
    }

    /// <summary>
    /// Escalation (task: a second fix round over the same findings) is a fact about the fix
    /// session that dispatched, so it rides on the same event the model does and reads back the
    /// same way — visible for a reader of <c>h9k task show</c> while the escalated round is the
    /// most recent, and cleared the moment a later fix round de-escalates.
    /// </summary>
    [Fact]
    public void Run_details_records_the_most_recent_fix_dispatches_escalation()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ReviewFixDispatched>(new ReviewFixDispatched(
            id, DomainId.New(), 1, 4485, Now, Now, AgentModel.Sonnet,
            Escalated: true, EscalationReason: "repeat round over the same findings (src/Auth.cs:42)")), view);

        view.LastFixSessionEscalated.Should().BeTrue();
        view.LastFixSessionEscalationReason.Should().Be("repeat round over the same findings (src/Auth.cs:42)");
        view.LastFixSessionEscalationCycle.Should().Be(1);

        // A later review pass (cycle 2) must not make the escalation line claim cycle 2 dispatched
        // the escalated fix — it did not, and ReviewCycle has already moved past it.
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 2, 4487, Now, Now, null, ReviewLens.Conformance)), view);

        view.ReviewCycle.Should().Be(2);
        view.LastFixSessionEscalationCycle.Should().Be(1, "no fix session has dispatched at cycle 2");

        projection.Apply(new FakeEvent<ReviewFixDispatched>(new ReviewFixDispatched(
            id, DomainId.New(), 2, 4486, Now, Now, AgentModel.Haiku)), view);

        view.LastFixSessionEscalated.Should().BeFalse("the later round de-escalated");
        view.LastFixSessionEscalationReason.Should().BeNull();
        view.LastFixSessionEscalationCycle.Should().Be(2);
    }

    [Fact]
    public void Run_list_item_walks_under_review_and_review_parked()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now)), view);
        view.State.Should().Be(RunState.UnderReview);

        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "disputed", Now)), view);
        view.State.Should().Be(RunState.ReviewParked);

        projection.Apply(new FakeEvent<ReviewParkResolved>(
            new ReviewParkResolved(id, ReviewVerdict.NeedsFixes, "fix it", Now, DomainId.New())), view);
        view.State.Should().Be(RunState.UnderReview);
    }

    /// <summary>
    /// The escape-hatch invariant's own record (task: every outside interaction a dispatched
    /// agent has is logged unconditionally): <c>h9k task log-interaction</c> appends this event,
    /// and <see cref="RunDetails.ExternalInteractions"/> is the read model
    /// <see cref="Hall9k.Daemon.Review.ReviewEngine"/> queries by task to carry a human-directed
    /// entry forward into a later review pass, the same way <see cref="ReviewParkResolutions"/>
    /// already carries a settled park ruling forward.
    /// </summary>
    [Fact]
    public void Run_details_keeps_every_logged_external_interaction_as_history()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = VerifiedRun(projection, id);

        projection.Apply(new FakeEvent<ExternalInteractionLogged>(new ExternalInteractionLogged(
            id, Now, "another agent session", "Shared this run's worktree path with it", false, null,
            DomainId.New())), view);
        projection.Apply(new FakeEvent<ExternalInteractionLogged>(new ExternalInteractionLogged(
            id, Now, "the operator", "Skip the workaround", true, "Real bug, ordered fixed",
            DomainId.New())), view);

        view.ExternalInteractions.Should().HaveCount(2, "every logged interaction is kept, not just the latest");
        view.ExternalInteractions[0].Should().BeEquivalentTo(
            new ExternalInteractionRecord(Now, "another agent session", "Shared this run's worktree path with it", false, null));
        view.ExternalInteractions[1].Should().BeEquivalentTo(
            new ExternalInteractionRecord(Now, "the operator", "Skip the workaround", true, "Real bug, ordered fixed"));
    }

    /// <summary>
    /// RunListItem gained no handlers for the pr-review task type's own two events
    /// (PrReviewEngine) when they landed, so it disagreed with RunDetails for the whole
    /// conformance-lens window — stuck reporting Verifying, a state meaning "the project's
    /// gates are running", for a task type whose gates never run at all.
    /// </summary>
    [Fact]
    public void Run_list_item_walks_the_pr_review_conformance_lens_the_same_way_run_details_does()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        view.State.Should().Be(RunState.Verifying, "the adversarial lens is this run's ordinary primary session");

        projection.Apply(new FakeEvent<PrReviewConformanceDispatched>(
            new PrReviewConformanceDispatched(id, DomainId.New(), 5501, Now, Now, AgentModel.Unknown)), view);
        view.State.Should().Be(RunState.UnderReview, "not stuck at Verifying for a task type whose gates never run");

        projection.Apply(new FakeEvent<PrReviewDelivered>(
            new PrReviewDelivered(id, "Walked and directed.", Now, DomainId.New())), view);
        view.State.Should().Be(RunState.UnderReview, "the daemon's own resume sweep signal, same as ReviewParkResolved");
    }

    private static RunDetails VerifiedRun(RunDetailsProjection projection, Guid id)
    {
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<VerificationPassed>(new VerificationPassed(id, Now)), view);
        return view;
    }
}
