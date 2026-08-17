using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The closeout walk (Decisions Log #18/#22) through the run read models: a PR-open run
/// observes failing checks, review feedback, a park, and finally the merge that gives
/// RunCompleted its reserved meaning.
/// </summary>
public sealed class RunCloseoutProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/7";

    [Fact]
    public void Run_details_walks_checks_failing_review_pending_and_completes_on_merge()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<PullRequestChecksFailed>(
            new PullRequestChecksFailed(id, ["build (ubuntu-latest)", "test (windows-latest)"], Now)), view);
        view.State.Should().Be(RunState.ChecksFailing);
        view.FailingChecks.Should().Equal("build (ubuntu-latest)", "test (windows-latest)");

        projection.Apply(new FakeEvent<ReviewFeedbackReceived>(
            new ReviewFeedbackReceived(id, 3, Now.AddMinutes(10))), view);
        view.State.Should().Be(RunState.ReviewPending);
        view.UnresolvedReviewThreads.Should().Be(3);

        DateTimeOffset mergedAt = Now.AddHours(1);
        projection.Apply(new FakeEvent<PullRequestMerged>(
            new PullRequestMerged(id, mergedAt, Now.AddHours(1).AddMinutes(3))), view);
        projection.Apply(new FakeEvent<RunCompleted>(new RunCompleted(id, mergedAt)), view);

        view.State.Should().Be(RunState.Completed, "the observed merge is what completes the run");
        view.PullRequestMergedAt.Should().Be(mergedAt);
        view.FinishedAt.Should().Be(mergedAt);
    }

    [Fact]
    public void Run_details_parks_with_the_reason_when_the_automatic_budget_is_spent()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<PullRequestChecksFailed>(
            new PullRequestChecksFailed(id, ["build"], Now)), view);
        projection.Apply(new FakeEvent<CloseoutParked>(
            new CloseoutParked(id, "CI checks failing. Automatic follow-up budget spent (2 run(s)).", Now)), view);

        view.State.Should().Be(RunState.CloseoutParked);
        view.State.IsTerminal.Should().BeFalse("a parked run still awaits the merge the monitor watches for");
        view.ParkedReason.Should().Contain("budget spent");
        view.FailureReason.Should().BeNull("parked is a waiting state, not a failure");
    }

    [Fact]
    public void Run_details_fails_honestly_when_the_pull_request_closes_without_merge()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<PullRequestClosed>(
            new PullRequestClosed(id, Now.AddHours(2), Now.AddHours(2).AddMinutes(1))), view);

        view.State.Should().Be(RunState.Failed);
        view.FailureReason.Should().Contain("closed without merge");
        view.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public void Run_list_item_mirrors_the_closeout_states()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/abc", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<PullRequestOpened>(new PullRequestOpened(id, PullRequestUrl, 7, Now)), view);

        projection.Apply(new FakeEvent<PullRequestChecksFailed>(new PullRequestChecksFailed(id, ["build"], Now)), view);
        view.State.Should().Be(RunState.ChecksFailing);

        projection.Apply(new FakeEvent<CloseoutParked>(new CloseoutParked(id, "budget spent", Now)), view);
        view.State.Should().Be(RunState.CloseoutParked);

        projection.Apply(new FakeEvent<RunCompleted>(new RunCompleted(id, Now.AddHours(1))), view);
        view.State.Should().Be(RunState.Completed);
        view.FinishedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Run_aggregate_replays_the_same_closeout_walk()
    {
        Guid id = DomainId.New();
        RunAggregate run = new();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/abc", ExecutorMode.Subscription, Now));
        run.Apply(new PullRequestOpened(id, PullRequestUrl, 7, Now));

        run.Apply(new PullRequestChecksFailed(id, ["build"], Now));
        run.State.Should().Be(RunState.ChecksFailing);
        run.FailingChecks.Should().Equal("build");

        run.Apply(new ReviewFeedbackReceived(id, 2, Now));
        run.State.Should().Be(RunState.ReviewPending);

        run.Apply(new CloseoutParked(id, "budget spent", Now));
        run.State.Should().Be(RunState.CloseoutParked);

        DateTimeOffset mergedAt = Now.AddDays(1);
        run.Apply(new PullRequestMerged(id, mergedAt, mergedAt.AddMinutes(2)));
        run.Apply(new RunCompleted(id, mergedAt));
        run.State.Should().Be(RunState.Completed);
        run.PullRequestMergedAt.Should().Be(mergedAt);
    }

    private static RunDetails AwaitingReviewRun(RunDetailsProjection projection, Guid id)
    {
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/abc", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<PullRequestOpened>(new PullRequestOpened(id, PullRequestUrl, 7, Now)), view);
        view.State.Should().Be(RunState.AwaitingReview);
        return view;
    }
}
