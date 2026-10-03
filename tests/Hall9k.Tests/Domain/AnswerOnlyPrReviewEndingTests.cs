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
/// What a delivered pr-review verdict does to the task once the review is resolved, and why an
/// answer-only task (decision dce39370: one comment answered on the owner's own pull request) ends
/// there instead of waiting on a pull request nobody should be reviewing.
/// </summary>
public sealed class AnswerOnlyPrReviewEndingTests
{
    private const string PullRequestUrl = "https://github.com/acme/widgets/pull/42";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void An_answer_only_task_ends_as_done_with_the_pull_request_url_and_opens_no_follow_through()
    {
        TaskAggregate task = ClaimedPrReviewTask(answerOnly: true);
        Guid runId = task.CurrentRunId!.Value;

        object ending = TaskDecider.ConcludeDeliveredPrReview(task, runId, PullRequestUrl, "abc123", Now);
        task.Apply((TaskCompleted)ending);

        ending.Should().BeOfType<TaskCompleted>().Which.PullRequestUrl.Should().Be(PullRequestUrl);
        task.State.Should().Be(TaskState.Done);
        task.PrReviewFollowThroughOpen.Should().BeFalse();
    }

    [Fact]
    public void A_full_review_still_opens_follow_through_on_the_pull_request()
    {
        TaskAggregate task = ClaimedPrReviewTask(answerOnly: false);
        Guid runId = task.CurrentRunId!.Value;

        object ending = TaskDecider.ConcludeDeliveredPrReview(task, runId, PullRequestUrl, "abc123", Now);
        task.Apply((PullRequestReviewFollowThroughOpened)ending);

        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewFollowThroughOpen.Should().BeTrue();
    }

    [Fact]
    public void A_full_review_with_no_readable_pull_request_still_completes()
    {
        TaskAggregate task = ClaimedPrReviewTask(answerOnly: false);

        object ending = TaskDecider.ConcludeDeliveredPrReview(task, task.CurrentRunId!.Value, null, null, Now);

        ending.Should().BeOfType<TaskCompleted>().Which.PullRequestUrl.Should().BeNull();
    }

    [Fact]
    public void A_task_minted_before_answer_only_existed_still_opens_follow_through()
    {
        // The event as an older stream wrote it: MintedTask true and no AnswerOnly field at all.
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(new PullRequestReviewMentionObserved(
            task.Id, PullRequestUrl, "IC_1", "ryan", "@brian?", "url", Now, Now,
            MintedTask: true, MentionedLogin: "brian"));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));

        object ending = TaskDecider.ConcludeDeliveredPrReview(task, task.CurrentRunId!.Value, PullRequestUrl, null, Now);

        task.AnswersMentionOnly.Should().BeFalse();
        ending.Should().BeOfType<PullRequestReviewFollowThroughOpened>();
    }

    [Fact]
    public void A_done_answer_only_task_is_never_swept_and_records_no_author_response()
    {
        TaskAggregate task = ClaimedPrReviewTask(answerOnly: true);
        task.Apply((TaskCompleted)TaskDecider.ConcludeDeliveredPrReview(
            task, task.CurrentRunId!.Value, PullRequestUrl, "abc123", Now));

        TaskDecider.AwaitsPrReviewFollowThrough(task).Should().BeFalse("the follow-through sweep selects only what this reports");

        Action recordResponse = () => TaskDecider.RecordPrReviewAuthorResponse(
            task, "acme/widgets#42 moved: 2 new commits", replyCount: 0, threadsWithReplies: 0, newCommitCount: 2,
            headMoved: true, reReviewNewlyRequested: false, interactiveSessionAddress: null, Now);
        Action observe = () => TaskDecider.ObservePrReviewFollowThrough(
            task, "brian", [], reReviewRequested: false, "def456", commitCount: 3, Now);

        recordResponse.Should().Throw<DomainConflictException>();
        observe.Should().Throw<DomainConflictException>();
    }

    private static TaskAggregate ClaimedPrReviewTask(bool answerOnly)
    {
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(new PullRequestReviewMentionObserved(
            task.Id, PullRequestUrl, "IC_1", "ryan", "@brian?", "url", Now, Now,
            MintedTask: true, MentionedLogin: "brian", AnswerOnly: answerOnly));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        return task;
    }

    private static TaskAggregate QueuedPrReviewTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review pull request acme/widgets#42", ["Walk the findings."],
            TaskType.PrReview, agentContext: "Imported from github-pr:acme/widgets#42.", constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#42"),
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }
}
