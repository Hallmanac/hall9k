using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Queries;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MentionFollowUpClaimResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TaskId = Guid.NewGuid();

    [Fact]
    public void A_mention_observed_after_the_claim_is_not_the_claimed_one()
    {
        PullRequestReviewMentionObserved member = Mention("IC_MEMBER");
        PullRequestReviewMentionObserved stranger = Mention("IC_STRANGER");

        MentionFollowUpClaimAnchors anchors = MentionFollowUpClaimResolver.Resolve(
            [member, Claim(Guid.Empty), stranger, Claim(Guid.NewGuid())]);

        anchors.Claimed.Should().Be(member);
    }

    [Fact]
    public void The_latest_mention_claim_is_the_one_a_pending_follow_up_belongs_to()
    {
        PullRequestReviewMentionObserved earlier = Mention("IC_EARLIER");
        PullRequestReviewMentionObserved later = Mention("IC_LATER");

        MentionFollowUpClaimAnchors anchors = MentionFollowUpClaimResolver.Resolve(
            [earlier, Claim(Guid.Empty), later, Claim(Guid.Empty), Mention("IC_STRANGER"), Claim(Guid.NewGuid())]);

        anchors.Claimed.Should().Be(later);
    }

    [Fact]
    public void A_claim_not_directly_after_a_mention_or_an_interactive_one_names_no_comment()
    {
        MentionFollowUpClaimAnchors anchors = MentionFollowUpClaimResolver.Resolve(
            [Mention("IC_A"), new TaskRequeued(TaskId, RequeueReason.PrReviewPreflightSafeMentionFollowUp, Now), Claim(Guid.Empty),
             Mention("IC_B"), Claim(Guid.Empty, interactive: true)]);

        anchors.Claimed.Should().BeNull();
        anchors.Only.Should().BeNull("two different comments were observed");
    }

    [Fact]
    public void The_minting_mention_is_never_a_claimed_one()
    {
        PullRequestReviewMentionObserved mint = Mention("IC_MINT") with { MintedTask = true, AnswerOnly = true };

        MentionFollowUpClaimAnchors anchors = MentionFollowUpClaimResolver.Resolve([mint, Claim(Guid.Empty)]);

        anchors.Claimed.Should().BeNull();
        anchors.Minting.Should().Be(mint);
    }

    [Fact]
    public void A_single_observed_comment_is_the_only_candidate_even_when_observed_twice()
    {
        PullRequestReviewMentionObserved once = Mention("IC_ONLY");
        PullRequestReviewMentionObserved again = Mention("IC_ONLY");

        MentionFollowUpClaimResolver.Resolve([once, again]).Only.Should().Be(again);
    }

    private static PullRequestReviewMentionObserved Mention(string commentId) =>
        new(TaskId, "https://github.com/acme/web/pull/1", commentId, "ryan", "@brian hi", $"u#{commentId}", Now, Now);

    private static TaskClaimed Claim(Guid nodeId, bool interactive = false) =>
        new(TaskId, nodeId, Guid.NewGuid(), 1, Guid.NewGuid(), Now,
            DependencyOverrideAcknowledged: false, DependencyOverrideCarriedForward: false, InteractiveMode: interactive);
}
