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
            new ReviewFeedbackReceived(id, 3, Now.AddMinutes(10), UnresolvedHumanThreadCount: 1)), view);
        view.State.Should().Be(RunState.ReviewPending);
        view.UnresolvedReviewThreads.Should().Be(3);
        view.UnresolvedHumanReviewThreads.Should().Be(1, "a person waiting on an answer is part of the observation");

        projection.Apply(new FakeEvent<ReviewErrored>(new ReviewErrored(
            id, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now.AddMinutes(20))), view);
        view.State.Should().Be(RunState.ReviewPending, "an errored review is pending review, never review-clean");
        view.ErroredReviewUrl.Should().Be($"{PullRequestUrl}#pullrequestreview-1");

        projection.Apply(new FakeEvent<ReviewRerequested>(new ReviewRerequested(
            id, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now.AddMinutes(20))), view);
        view.State.Should().Be(RunState.ReviewPending);
        view.ReviewRerequestCount.Should().Be(1);

        DateTimeOffset mergedAt = Now.AddHours(1);
        projection.Apply(new FakeEvent<PullRequestMerged>(
            new PullRequestMerged(id, mergedAt, Now.AddHours(1).AddMinutes(3))), view);
        projection.Apply(new FakeEvent<RunCompleted>(new RunCompleted(id, mergedAt)), view);

        view.State.Should().Be(RunState.Completed, "the observed merge is what completes the run");
        view.PullRequestMergedAt.Should().Be(mergedAt);
        view.FinishedAt.Should().Be(mergedAt);
    }

    /// <summary>
    /// An observation written before reviewers other than Copilot were counted never looked at
    /// authorship, so it replays as unknown rather than as "no humans" (Decisions Log #62, and
    /// the never-guess rule).
    /// </summary>
    [Fact]
    public void A_thread_observation_from_before_authorship_was_counted_replays_as_unknown()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<ReviewFeedbackReceived>(new ReviewFeedbackReceived(id, 2, Now)), view);

        view.UnresolvedReviewThreads.Should().Be(2);
        view.UnresolvedHumanReviewThreads.Should().BeNull("zero would claim an observation that never happened");
    }

    /// <summary>
    /// The countersign asks a question; it does not receive a finding, so the run keeps
    /// awaiting review and the monitor keeps watching (Decisions Log #62).
    /// </summary>
    [Fact]
    public void A_countersign_rerequest_counts_without_moving_the_run()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<ReviewRerequestedAfterFixes>(
            new ReviewRerequestedAfterFixes(id, ["copilot-pull-request-reviewer", "teammate"], 1, Now)), view);

        view.ReviewRerequestsAfterFixes.Should().Be(1);
        view.ReviewRerequestCount.Should().Be(0, "the errored-review counter is a different question");
        view.State.Should().Be(RunState.AwaitingReview);
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

        projection.Apply(new FakeEvent<ReviewErrored>(new ReviewErrored(
            id, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now)), view);
        view.State.Should().Be(RunState.ReviewPending);

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

        run.Apply(new ReviewErrored(id, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now));
        run.State.Should().Be(RunState.ReviewPending);
        run.ErroredReviewUrl.Should().Be($"{PullRequestUrl}#pullrequestreview-1");

        run.Apply(new ReviewRerequested(id, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now));
        run.ReviewRerequestCount.Should().Be(1);

        run.Apply(new CloseoutParked(id, "budget spent", Now));
        run.State.Should().Be(RunState.CloseoutParked);

        DateTimeOffset mergedAt = Now.AddDays(1);
        run.Apply(new PullRequestMerged(id, mergedAt, mergedAt.AddMinutes(2)));
        run.Apply(new RunCompleted(id, mergedAt));
        run.State.Should().Be(RunState.Completed);
        run.PullRequestMergedAt.Should().Be(mergedAt);
    }

    /// <summary>
    /// h9k pr resolve's budget reset is recorded twice (Decisions Log #77, backlog 45): the
    /// reset itself lands on the task stream as TaskReopened(Automatic: false), and this event
    /// records the same grant on the run the human resolved, so the run's own history shows a
    /// human touched it.
    /// </summary>
    [Fact]
    public void A_human_grant_is_recorded_on_the_run()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = AwaitingReviewRun(projection, id);

        projection.Apply(new FakeEvent<CloseoutBudgetGranted>(
            new CloseoutBudgetGranted(id, "Unresolved review comments on the pull request.", Now.AddHours(1))), view);

        view.HumanGrantedAt.Should().Be(Now.AddHours(1));

        RunAggregate run = new();
        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/abc", ExecutorMode.Subscription, Now));
        run.Apply(new CloseoutBudgetGranted(id, "CI checks failing on the pull request.", Now.AddHours(1)));
        run.HumanGrantedAt.Should().Be(Now.AddHours(1));
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
