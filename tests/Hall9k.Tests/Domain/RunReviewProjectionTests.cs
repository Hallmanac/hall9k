using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
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
