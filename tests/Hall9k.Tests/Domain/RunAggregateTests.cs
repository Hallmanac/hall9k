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

        Guid reviewSession = DomainId.New();
        run.Apply(new ReviewDispatched(id, reviewSession, Cycle: 1, ProcessId: 5001, Now, Now));
        run.State.Should().Be(RunState.UnderReview);
        run.State.IsLive.Should().BeTrue("a review session is a live process the daemon watches");
        run.ReviewPhase.Should().Be(ReviewPhase.AwaitingVerdict);
        run.ActiveReviewSessionId.Should().Be(reviewSession);
        run.ActiveReviewSessionIsFix.Should().BeFalse();

        run.Apply(new TokensRecorded(id, 30_000, 5_000, null, Now));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded);
        run.LastReviewVerdict.Should().Be(ReviewVerdict.NeedsFixes);
        run.ActiveReviewSessionId.Should().BeNull("the verdict retires the review session");

        Guid fixSession = DomainId.New();
        run.Apply(new ReviewFixDispatched(id, fixSession, Cycle: 1, ProcessId: 5002, Now, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.AwaitingFix);
        run.ReviewFixRuns.Should().Be(1, "the fix leg is what the automatic budget counts");
        run.ActiveReviewSessionIsFix.Should().BeTrue();

        run.Apply(new TokensRecorded(id, 40_000, 8_000, null, Now));
        run.Apply(new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.Reverify, "a fixed outcome re-runs the gates before the next review");

        run.Apply(new VerificationPassed(id, Now));
        run.Apply(new ReviewDispatched(id, DomainId.New(), Cycle: 2, ProcessId: 5003, Now, Now));
        run.Apply(new TokensRecorded(id, 25_000, 4_000, null, Now));
        run.Apply(new ReviewCompleted(id, 2, ReviewVerdict.MergeReady, Now));
        run.ReviewPhase.Should().Be(ReviewPhase.MergeReady);
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
