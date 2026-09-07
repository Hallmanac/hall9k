using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A reviewer's review lap through the aggregate and the details read model (Decisions Log
/// #149): the lap opens, and the verdict closes it while leaving behind what was actually
/// posted. Two facts here are load-bearing beyond their obvious use, and both are checked:
/// <c>ReviewLapOpen</c> is what the daemon's startup adoption reads to leave a human's lap
/// alone, and a blank <c>WorktreePath</c> means the checkout was skipped rather than that it
/// lives at the empty path.
/// </summary>
public sealed class ReviewLapProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_a_lap_records_the_run_it_rides_on_and_the_checkout_it_reads_in()
    {
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        TaskAggregate task = Claimed(id, runId);

        task.Apply(new PullRequestReviewLapOpened(
            id, runId, "/tmp/wt-pr-42", "https://github.com/acme/web/pull/42", Now, DomainId.New()));

        task.ReviewLapOpen.Should().BeTrue();
        task.ReviewLapRunId.Should().Be(runId);
        task.ReviewLapWorktreePath.Should().Be("/tmp/wt-pr-42");
        task.State.Should().Be(TaskState.Claimed, "the lap is provenance — it moves nothing");
    }

    [Fact]
    public void A_lap_with_no_checkout_records_the_absence_rather_than_an_empty_path()
    {
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        TaskAggregate task = Claimed(id, runId);

        task.Apply(new PullRequestReviewLapOpened(
            id, runId, string.Empty, "https://github.com/acme/web/pull/42", Now, DomainId.New()));

        task.ReviewLapOpen.Should().BeTrue();
        task.ReviewLapWorktreePath.Should().BeNull(
            "--no-worktree skipped the checkout, and an absence handed on as \"\" is a path something may try to remove");
    }

    [Fact]
    public void The_verdict_closes_the_lap_and_keeps_the_run_it_rode_on()
    {
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        TaskAggregate task = Claimed(id, runId);
        task.Apply(new PullRequestReviewLapOpened(
            id, runId, "/tmp/wt-pr-42", "https://github.com/acme/web/pull/42", Now, DomainId.New()));

        task.Apply(new PullRequestReviewVerdictDelivered(
            id, ReviewerVerdict.ChangesRequested, "Two real defects.",
            ["src/Program.cs:42: this swallows the cancellation"],
            "0f1e2d3c4b5a", "https://github.com/acme/web/pull/42#pullrequestreview-1",
            Now.AddHours(1), DomainId.New()));

        task.ReviewLapOpen.Should().BeFalse("the verdict is the end of the lap");
        task.ReviewerVerdict.Should().Be(ReviewerVerdict.ChangesRequested);
        task.ReviewLapRunId.Should().Be(
            runId, "a verdict attributed to no run would be a verdict with nothing to finalize");
        task.State.Should().Be(
            TaskState.Claimed,
            "the verdict does not complete the task — the run's own PrReviewDelivered reaching PrReviewEngine does");
    }

    /// <summary>
    /// The verdict is not the only way a lap ends. A reviewer can walk away — <c>h9k task
    /// release</c>, which a lap's own interactive claim accepts — and from there the verdict that
    /// would close the lap is unreachable, because <c>h9k pr approve</c> refuses a task with no
    /// current run to record one against. So a flag left standing could only ever be wrong, and
    /// the daemon read exactly that flag on a LATER, automated run of the same task and skipped
    /// its adoption as "a reviewer owns it" — a dead agent never failed, a live one never
    /// re-monitored (independent pre-PR review, cycle 1, adversarial lens). The run id survives:
    /// a lap did run, on that run, whether or not it ended in a verdict.
    /// </summary>
    [Fact]
    public void Releasing_the_claim_ends_the_lap_it_rode_on()
    {
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        TaskAggregate task = Claimed(id, runId);
        task.Apply(new PullRequestReviewLapOpened(
            id, runId, "/tmp/wt-pr-42", "https://github.com/acme/web/pull/42", Now, DomainId.New()));

        task.Apply(new TaskRequeued(id, RequeueReason.HumanRequested, Now.AddHours(1), ClearInteractiveMode: true));

        task.ReviewLapOpen.Should().BeFalse(
            "the claim is gone, so the verdict that would close this lap can never be recorded");
        task.CurrentRunId.Should().BeNull();
        task.ReviewLapRunId.Should().Be(runId, "a lap did run on that run — that stays true");
        task.ReviewerVerdict.Should().Be(
            ReviewerVerdict.Unknown, "walking away is not a verdict, and must never read as one");
    }

    /// <summary>
    /// The same rule on the read model, which is the copy that matters operationally:
    /// <c>RunSupervisor.AdoptOrphansAsync</c> reads <c>TaskDetails.ReviewLapOpen</c>, not the
    /// aggregate's.
    /// </summary>
    [Fact]
    public void The_details_view_ends_the_lap_when_the_claim_goes_back()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Review pull request acme/web#42", ["the verdict is submitted"],
            TaskType.PrReview, null, null, null, Now, DomainId.New())));
        projection.Apply(new FakeEvent<PullRequestReviewLapOpened>(new PullRequestReviewLapOpened(
            id, runId, "/tmp/wt-pr-42", "https://github.com/acme/web/pull/42", Now, DomainId.New())), view);

        projection.Apply(
            new FakeEvent<TaskRequeued>(new TaskRequeued(
                id, RequeueReason.HumanRequested, Now.AddHours(1), ClearInteractiveMode: true)),
            view);

        view.ReviewLapOpen.Should().BeFalse();
        view.ReviewLapRunId.Should().Be(runId, "provenance of the lap that ran survives the release");
    }

    [Fact]
    public void A_pr_review_task_that_never_took_a_lap_carries_no_verdict()
    {
        TaskAggregate task = Claimed(DomainId.New(), DomainId.New());

        task.ReviewLapOpen.Should().BeFalse();
        task.ReviewLapRunId.Should().BeNull();
        task.ReviewerVerdict.Should().Be(
            ReviewerVerdict.Unknown,
            "a task closed the older way — h9k review resolve --merge-ready, nothing posted — must not read as an approval");
    }

    [Fact]
    public void The_details_view_keeps_what_was_actually_posted()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Review pull request acme/web#42", ["the verdict is submitted"],
            TaskType.PrReview, null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<PullRequestReviewLapOpened>(new PullRequestReviewLapOpened(
            id, runId, "/tmp/wt-pr-42", "https://github.com/acme/web/pull/42", Now, DomainId.New())), view);
        view.ReviewLapOpen.Should().BeTrue();
        view.ReviewLapRunId.Should().Be(runId);
        view.ReviewLapWorktreePath.Should().Be("/tmp/wt-pr-42");

        projection.Apply(new FakeEvent<PullRequestReviewVerdictDelivered>(new PullRequestReviewVerdictDelivered(
            id, ReviewerVerdict.Approved, "Reads clean.", [], "0f1e2d3c4b5a",
            "https://github.com/acme/web/pull/42#pullrequestreview-1", Now.AddHours(1), DomainId.New())), view);

        view.ReviewLapOpen.Should().BeFalse();
        view.ReviewerVerdict.Should().Be(ReviewerVerdict.Approved);
        view.ReviewerVerdictNote.Should().Be("Reads clean.");
        view.ReviewerVerdictHeadSha.Should().Be(
            "0f1e2d3c4b5a", "the head the review was submitted against can have moved since");
        view.ReviewerVerdictReviewUrl.Should().Be("https://github.com/acme/web/pull/42#pullrequestreview-1");
        view.ReviewerVerdictFindings.Should().BeEmpty("an approval carries only its note");
    }

    /// <summary>
    /// GitHub answers a successful post with a body this platform then parses. When that body
    /// carries no URL the post still happened, so the verdict is recorded with the gap admitted
    /// rather than a URL composed from the pull request and a review id nobody read.
    /// </summary>
    [Fact]
    public void A_verdict_whose_review_url_could_not_be_read_records_the_gap()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Review pull request acme/web#42", ["the verdict is submitted"],
            TaskType.PrReview, null, null, null, Now, DomainId.New())));
        projection.Apply(new FakeEvent<PullRequestReviewVerdictDelivered>(new PullRequestReviewVerdictDelivered(
            id, ReviewerVerdict.Approved, "Reads clean.", [], "0f1e2d3c4b5a", null,
            Now, DomainId.New())), view);

        view.ReviewerVerdict.Should().Be(ReviewerVerdict.Approved);
        view.ReviewerVerdictReviewUrl.Should().BeNull();
    }

    private static TaskAggregate Claimed(Guid id, Guid runId)
    {
        TaskAggregate task = new();
        TaskAdded added = new(
            id, DomainId.New(), "Review pull request acme/web#42", ["the verdict is submitted"],
            TaskType.PrReview, null, null, null, Now, DomainId.New());
        task.Apply(added);
        task.Apply(new TaskPublished(id, Now, DomainId.New()));
        task.Apply(new TaskAssigned(id, DomainId.New(), [], Now, DomainId.New()));
        task.Apply(new TaskClaimed(id, Guid.Empty, DomainId.New(), 1, runId, Now, InteractiveMode: true));
        return task;
    }
}
