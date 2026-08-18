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
