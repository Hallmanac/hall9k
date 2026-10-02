using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A mention follow-up runs only when this install's own login is the one the stored comment tags
/// and the comment is not its own. These tests replay AgelessRx/arx-platform#2166 from the task
/// owner's side with no database, no branch and no GitHub: Brian (Hallmanac) answered Taylor in a
/// comment that tagged @taylor-dennison, Taylor's install attached it to Brian's task and claimed
/// a follow-up, a pre-flight requeued it, and Brian's own dispatcher then claimed the pending
/// follow-up. The pre-launch gate must refuse it, and the skip must hand the task back.
/// </summary>
public sealed class MentionFollowUpSkipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 21, 21, 23, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private const string PullRequestUrl = "https://github.com/AgelessRx/arx-platform/pull/2166";
    private const string BrianLogin = "Hallmanac";
    private const string TaylorLogin = "taylor-dennison";
    private const string BrianReplyToTaylor = "@taylor-dennison yes, that endpoint is behind the flag now.";

    [Fact]
    public void Gate_refuses_a_comment_that_tags_another_login()
    {
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(BrianLogin, "ryan", BrianReplyToTaylor);

        gate.Proceed.Should().BeFalse();
        gate.Reason.Should().Contain("by ryan does not tag Hallmanac");
    }

    [Fact]
    public void Gate_refuses_a_comment_this_installs_own_login_wrote_even_when_it_tags_that_login()
    {
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(BrianLogin, "hallmanac", "note to self @Hallmanac");

        gate.Proceed.Should().BeFalse();
        gate.Reason.Should().Contain("written by Hallmanac");
    }

    [Fact]
    public void Gate_refuses_when_the_own_login_could_not_be_read()
    {
        MentionFollowUpGate unreadable = MentionFollowUpGate.Decide(null, TaylorLogin, "@Hallmanac thoughts?");
        MentionFollowUpGate blank = MentionFollowUpGate.Decide("  ", TaylorLogin, "@Hallmanac thoughts?");

        unreadable.Proceed.Should().BeFalse();
        blank.Proceed.Should().BeFalse();
    }

    [Theory]
    [InlineData("@hallmanac can you look at this?")]
    [InlineData("cc @HALLMANAC, thoughts?")]
    public void Gate_lets_a_comment_by_someone_else_that_tags_this_login_through(string body)
    {
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(BrianLogin, TaylorLogin, body);

        gate.Proceed.Should().BeTrue();
        gate.Reason.Should().BeEmpty();
    }

    [Fact]
    public void Gate_does_not_read_a_longer_login_as_a_tag_of_this_one()
    {
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(BrianLogin, TaylorLogin, "@Hallmanac-2 can you look?");

        gate.Proceed.Should().BeFalse();
    }

    [Fact]
    public void Gate_refuses_a_stored_comment_with_no_body()
    {
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(BrianLogin, TaylorLogin, string.Empty);

        gate.Proceed.Should().BeFalse();
    }

    [Fact]
    public void ObservePrReviewMention_records_the_tagged_login_beside_the_author()
    {
        TaskAggregate task = WaitingPrReviewTask();

        PullRequestReviewMentionObserved observed = TaskDecider.ObservePrReviewMention(
            task, PullRequestUrl, "IC_1", BrianLogin, BrianReplyToTaylor, "url", Now, Now, mentionedLogin: TaylorLogin);
        task.Apply(observed);

        observed.MentionedLogin.Should().Be(TaylorLogin);
        task.LatestMentionAuthorLogin.Should().Be(BrianLogin);
        task.LatestMentionTaggedLogin.Should().Be(TaylorLogin);
    }

    [Fact]
    public void A_mention_from_an_older_node_records_no_tagged_login()
    {
        TaskAggregate task = WaitingPrReviewTask();

        task.Apply(TaskDecider.ObservePrReviewMention(
            task, PullRequestUrl, "IC_1", BrianLogin, BrianReplyToTaylor, "url", Now, Now));

        task.LatestMentionTaggedLogin.Should().BeNull("an older node's event names no tagged login, and null is unknown, never this install's own");
    }

    [Fact]
    public void Replaying_2166_from_the_owners_side_skips_the_pending_follow_up_and_hands_the_task_back()
    {
        TaskAggregate task = WaitingPrReviewTask();
        Guid reviewRunId = task.CurrentRunId!.Value;
        task.Apply(TaskDecider.ObservePrReviewMention(
            task, PullRequestUrl, "IC_5940862269", BrianLogin, BrianReplyToTaylor, "url", Now, Now,
            mentionedLogin: TaylorLogin));

        // Taylor's install claims the follow-up with the sentinel claim, its pre-flight comes back
        // safe, and the task is requeued still assigned to Brian.
        Guid taylorRunId = DomainId.New();
        task.Apply(TaskDecider.ClaimForMentionFollowUp(task, Owner, taylorRunId, Now, reportParkedAwaitingWalk: false));
        task.Apply(TaskDecider.Requeue(task, RequeueReason.PrReviewPreflightSafeMentionFollowUp, Now));
        task.PendingMentionFollowUpAfterPreflight.Should().BeTrue();

        // Brian's dispatcher claims the pending follow-up under an ordinary claim.
        Guid brianNode = DomainId.New();
        Guid brianRunId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, brianNode, Owner, brianRunId, Now));

        // The launch's gate reads the stored comment against Brian's own login.
        MentionFollowUpGate gate = MentionFollowUpGate.Decide(
            BrianLogin, task.LatestMentionAuthorLogin!, task.LatestMentionBody!);
        gate.Proceed.Should().BeFalse();

        PullRequestReviewMentionFollowUpSkipped skipped = TaskDecider.SkipPrReviewMentionFollowUp(
            task, brianRunId, task.LatestMentionCommentId!, gate.Reason, Now);
        task.Apply(skipped);

        skipped.ReturnedToState.Should().Be(TaskState.AwaitingAuthor);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.CurrentRunId.Should().Be(reviewRunId, "the task names the review it is waiting on again, as it did before any claim");
        task.PendingMentionFollowUpAfterPreflight.Should().BeFalse("the next dispatch must not try the same comment again");
        task.RunIds.Should().NotContain(brianRunId, "no run stream was ever opened under the skipped claim");
        TaskDecider.AwaitsPrReviewMentionFollowUp(task, reportParkedAwaitingWalk: false)
            .Should().BeTrue("a later comment that does tag this login can still claim a follow-up");
    }

    [Fact]
    public void Skipping_a_direct_claim_returns_the_task_to_the_state_it_held_before_the_claim()
    {
        TaskAggregate task = WaitingPrReviewTask();
        Guid reviewRunId = task.CurrentRunId!.Value;
        Guid claimRunId = DomainId.New();
        task.Apply(TaskDecider.ClaimForMentionFollowUp(task, Owner, claimRunId, Now, reportParkedAwaitingWalk: false));
        task.State.Should().Be(TaskState.Claimed);

        task.Apply(TaskDecider.SkipPrReviewMentionFollowUp(task, claimRunId, "IC_1", "not for this install", Now));

        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.CurrentRunId.Should().Be(reviewRunId);
        task.ClaimedByNodeId.Should().Be(task.ClaimedByNodeIdBeforeLatestClaim);
    }

    [Fact]
    public void Skipping_returns_a_needs_human_task_to_needs_human()
    {
        TaskAggregate task = WaitingPrReviewTask();
        task.Apply(TaskDecider.RecordPrReviewAuthorResponse(
            task, "owner/repo#42 moved since your review: 1 reply in 1 thread.", replyCount: 1,
            threadsWithReplies: 1, newCommitCount: null, headMoved: false, reReviewNewlyRequested: false,
            interactiveSessionAddress: null, Now));
        Guid claimRunId = DomainId.New();
        task.Apply(TaskDecider.ClaimForMentionFollowUp(task, Owner, claimRunId, Now, reportParkedAwaitingWalk: false));
        task.Apply(TaskDecider.Requeue(task, RequeueReason.PrReviewPreflightRetryMentionFollowUp, Now));
        Guid secondRunId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, secondRunId, Now));

        task.Apply(TaskDecider.SkipPrReviewMentionFollowUp(task, secondRunId, "IC_1", "not for this install", Now));

        task.State.Should().Be(TaskState.NeedsHuman);
    }

    [Fact]
    public void Skipping_a_claim_the_task_no_longer_holds_is_refused()
    {
        TaskAggregate task = WaitingPrReviewTask();
        Guid staleRunId = DomainId.New();
        task.Apply(TaskDecider.ClaimForMentionFollowUp(task, Owner, staleRunId, Now, reportParkedAwaitingWalk: false));
        task.Apply(TaskDecider.Requeue(task, RequeueReason.PrReviewPreflightSafeMentionFollowUp, Now));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));

        Action act = () => TaskDecider.SkipPrReviewMentionFollowUp(task, staleRunId, "IC_1", "not for this install", Now);

        act.Should().Throw<DomainConflictException>();
    }

    private static TaskAggregate WaitingPrReviewTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review pull request AgelessRx/arx-platform#2166",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, agentContext: "Imported from github-pr:AgelessRx/arx-platform#2166.", constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.GitHubPullRequest, "AgelessRx/arx-platform#2166"),
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));
        task.Apply(TaskDecider.OpenPrReviewFollowThrough(
            task, task.CurrentRunId!.Value, PullRequestUrl, headSha: null, Now));
        return task;
    }
}
