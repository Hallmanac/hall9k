using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Auto-pr-review's second trigger (idea 2f079bcd): recording a GitHub comment that mentioned the
/// install's own login, and the mention follow-up's own claim — the fourth sibling of
/// <see cref="TaskDecider.ClaimInteractively"/>, <see cref="TaskDecider.ClaimDeliberately"/> and
/// <see cref="TaskDecider.ClaimForScopedReviewLap"/>.
/// </summary>
public sealed class PullRequestReviewMentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void ObservePrReviewMention_records_the_comments_own_fields()
    {
        TaskAggregate task = QueuedPrReviewTask();

        PullRequestReviewMentionObserved observed = TaskDecider.ObservePrReviewMention(
            task, "https://github.com/acme/widgets/pull/42", "IC_1", "ryan",
            "@brian what do you think?", "https://github.com/acme/widgets/pull/42#issuecomment-1", Now, Now);
        task.Apply(observed);

        task.LatestMentionCommentId.Should().Be("IC_1");
        task.LatestMentionAuthorLogin.Should().Be("ryan");
        task.LatestMentionBody.Should().Be("@brian what do you think?");
        task.LatestMentionUrl.Should().Be("https://github.com/acme/widgets/pull/42#issuecomment-1");
        task.LatestMentionCreatedAt.Should().Be(Now);
    }

    [Fact]
    public void ObservePrReviewMention_never_touches_state()
    {
        TaskAggregate task = QueuedPrReviewTask();
        TaskState before = task.State;

        task.Apply(TaskDecider.ObservePrReviewMention(
            task, "https://github.com/acme/widgets/pull/42", "IC_1", "ryan", "@brian?", "url", Now, Now));

        task.State.Should().Be(before, "a mention attaches to whatever state the task is already in");
    }

    [Fact]
    public void ObservePrReviewMention_refuses_a_task_that_is_not_pr_review()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting", ["done"], TaskType.Feature, null, null, null,
            Now, Owner));

        Action act = () => TaskDecider.ObservePrReviewMention(
            task, "https://github.com/acme/widgets/pull/42", "IC_1", "ryan", "@brian?", "url", Now, Now);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void ObservePrReviewMention_refuses_a_blank_pull_request_url()
    {
        TaskAggregate task = QueuedPrReviewTask();

        Action act = () => TaskDecider.ObservePrReviewMention(
            task, string.Empty, "IC_1", "ryan", "@brian?", "url", Now, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void ObservePrReviewMention_refuses_a_blank_comment_id()
    {
        TaskAggregate task = QueuedPrReviewTask();

        Action act = () => TaskDecider.ObservePrReviewMention(
            task, "https://github.com/acme/widgets/pull/42", string.Empty, "ryan", "@brian?", "url", Now, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void ClaimForMentionFollowUp_claims_from_AwaitingAuthor()
    {
        TaskAggregate task = WaitingPrReviewTask();
        Guid runId = DomainId.New();

        TaskClaimed claimed = TaskDecider.ClaimForMentionFollowUp(task, Owner, runId, Now, reportParkedAwaitingWalk: false);

        claimed.RunId.Should().Be(runId);
        claimed.InteractiveMode.Should().BeFalse("a GitHub mention is the daemon's own go signal, not a human at a terminal");
        claimed.LeaseGeneration.Should().Be(task.LeaseGeneration + 1);
    }

    [Fact]
    public void ClaimForMentionFollowUp_claims_from_NeedsHuman_when_the_report_is_already_parked()
    {
        TaskAggregate task = WaitingPrReviewTask();
        task.Apply(TaskDecider.RecordPrReviewAuthorResponse(
            task, "owner/repo#42 moved since your review: 1 reply in 1 thread.", replyCount: 1,
            threadsWithReplies: 1, newCommitCount: null, headMoved: false, reReviewNewlyRequested: false,
            interactiveSessionAddress: null, Now));
        task.State.Should().Be(TaskState.NeedsHuman);

        TaskClaimed claimed = TaskDecider.ClaimForMentionFollowUp(task, Owner, DomainId.New(), Now, reportParkedAwaitingWalk: false);

        claimed.InteractiveMode.Should().BeFalse();
    }

    [Fact]
    public void ClaimForMentionFollowUp_claims_from_Claimed_when_the_report_is_parked_and_unresolved()
    {
        // The state PrReviewEngine.ComposeReportAndParkAsync's own doc names: a report parked and
        // not yet walked leaves the TASK Claimed, since the park lives on the run stream
        // (RunState.ReviewParked), never the task's own. AwaitsPrReviewFollowThrough alone cannot
        // see this — reportParkedAwaitingWalk is the daemon's own observation of the run stream,
        // handed in as a fact (independent pre-PR review, cycle 1, both lenses).
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.State.Should().Be(TaskState.Claimed);

        TaskClaimed claimed = TaskDecider.ClaimForMentionFollowUp(task, Owner, DomainId.New(), Now, reportParkedAwaitingWalk: true);

        claimed.InteractiveMode.Should().BeFalse();
    }

    [Fact]
    public void ClaimForMentionFollowUp_refuses_a_Claimed_task_when_the_report_is_not_actually_parked()
    {
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));

        Action act = () => TaskDecider.ClaimForMentionFollowUp(task, Owner, DomainId.New(), Now, reportParkedAwaitingWalk: false);

        act.Should().Throw<DomainConflictException>();
    }

    [Fact]
    public void ClaimForMentionFollowUp_refuses_a_task_with_no_posted_review_being_followed_through()
    {
        TaskAggregate task = QueuedPrReviewTask();

        Action act = () => TaskDecider.ClaimForMentionFollowUp(task, Owner, DomainId.New(), Now, reportParkedAwaitingWalk: false);

        act.Should().Throw<DomainConflictException>();
    }

    private static TaskAggregate QueuedPrReviewTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review pull request acme/widgets#42",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, agentContext: "Imported from github-pr:acme/widgets#42.", constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#42"),
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }

    /// <summary>A pr-review task whose review has already posted and is waiting on its pull request — the pair of states <see cref="TaskDecider.AwaitsPrReviewFollowThrough"/> admits.</summary>
    private static TaskAggregate WaitingPrReviewTask()
    {
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.OpenPrReviewFollowThrough(
            task, task.CurrentRunId!.Value, "https://github.com/acme/widgets/pull/42", headSha: null, Now));
        return task;
    }
}
