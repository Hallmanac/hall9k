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
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded, "either lens finding real problems needs fixes");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        run.InFlightReviewPasses.Should().BeEmpty("the verdicts retire both review sessions");
        run.CompletedReviewPasses.Select(pass => pass.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial], "the cycle records which lens said what");

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
            id, DomainId.New(), Cycle: 2, ProcessId: 5003, Now, Now, Lens: ReviewLens.Conformance));
        run.Apply(new ReviewDispatched(
            id, DomainId.New(), Cycle: 2, ProcessId: 5013, Now, Now, Lens: ReviewLens.Adversarial));
        run.CompletedReviewPasses.Should().BeEmpty("a new cycle starts with no answers, only the last cycle's history");
        run.Apply(new TokensRecorded(id, 25_000, 4_000, null, Now));
        run.Apply(new ReviewPassCompleted(id, 2, ReviewLens.Conformance, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewPassCompleted(id, 2, ReviewLens.Adversarial, ReviewVerdict.MergeReady, Now));
        run.Apply(new ReviewCompleted(id, 2, ReviewVerdict.MergeReady, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady, "merge-ready takes BOTH lenses clean");
        run.ReviewCycle.Should().Be(2);

        run.InputTokens.Should().Be(195_000, "review and fix sessions record tokens like any other session");
        run.OutputTokens.Should().Be(37_000);

        run.Apply(new PullRequestOpened(id, "https://github.com/x/y/pull/8", 8, Now));
        run.State.Should().Be(RunState.AwaitingReview);
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
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
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
    public void Review_park_resolved_needs_fixes_restores_the_budget_and_carries_the_human_findings()
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
        run.Apply(new ReviewParked(id, "Budget spent.", Now));
        run.ReviewFixRuns.Should().Be(1);

        run.Apply(new ReviewParkResolved(
            id, ReviewVerdict.NeedsFixes, "The limiter finding is real; fix it.", Now, DomainId.New()));

        run.State.Should().Be(RunState.UnderReview);
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded);
        run.ReviewFixRuns.Should().Be(0, "the human asking is a fresh grant (log #22)");
        run.PendingHumanFindings.Should().Be("The limiter finding is real; fix it.");

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 2, 5004, Now, Now));
        run.PendingHumanFindings.Should().BeNull("the dispatch consumed the human findings");
        run.ReviewFixRuns.Should().Be(1);
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
