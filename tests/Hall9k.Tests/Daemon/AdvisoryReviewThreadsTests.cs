using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The test that decides whether a person's unresolved thread buys a follow-up lap (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own).
/// Every case here is about which way it errs: toward dispatching, always, because a false
/// advisory leaves a person unanswered while a false ask costs one lap that drafts and parks.
/// </summary>
public sealed class AdvisoryReviewThreadsTests
{
    [Theory]
    [InlineData("Why a canary value rather than the sentinel?")]
    [InlineData("Could you pull this into its own method")]
    [InlineData("Please use the existing helper.")]
    [InlineData("This should be the sentinel.")]
    [InlineData("nit: the name reads backwards")]
    [InlineData("Consider extracting the loop body.")]
    [InlineData("I'd suggest naming it Canonical.")]
    [InlineData("wdyt")]
    public void Text_that_asks_for_an_answer_or_a_change_asks_something(string body) =>
        AdvisoryReviewThreads.AsksSomething(body).Should().BeTrue();

    /// <summary>
    /// The declarative defect report, which is the most ordinary shape a substantive inline review
    /// comment takes: no question mark and no request phrasing, and still the one thread class
    /// nobody can leave sitting. Reading these as remarks left a real defect report un-actioned
    /// until somebody noticed by hand (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Theory]
    [InlineData("This is off by one: the loop starts at 1.")]
    [InlineData("This throws when the list is empty.")]
    [InlineData("The two branches are backwards.")]
    [InlineData("That swallows the cancellation.")]
    [InlineData("The projection is stale after a rename.")]
    [InlineData("It doesn't handle a null author.")]
    [InlineData("There's a typo in the event name.")]
    [InlineData("This breaks the pre-approved merge path.")]
    public void Text_that_reports_a_defect_asks_something_without_asking(string body) =>
        AdvisoryReviewThreads.AsksSomething(body).Should().BeTrue();

    /// <summary>
    /// The origin incident's own wording is the important case here: "my own PR needs the same
    /// pattern" is a remark about somebody else's branch, which is why bare "needs" is
    /// deliberately not one of the request phrases (arx-platform PR #2021, 2026-09-09).
    /// </summary>
    [Theory]
    [InlineData("Nice, my own PR needs the same pattern.")]
    [InlineData("TIL. Copying this into the other service.")]
    [InlineData("This is the bit I could never get right, good catch.")]
    [InlineData("LGTM")]
    [InlineData("Changed my mind about the sentinel, this reads better.")]
    [InlineData("I traced this through last week. Good to see it written down.")]
    public void Text_that_asks_nothing_asks_nothing(string body) =>
        AdvisoryReviewThreads.AsksSomething(body).Should().BeFalse();

    /// <summary>
    /// The one place the safety does not come from the thread's own text: a body the provider
    /// reported as empty asks nothing it can be read to ask, so what keeps this honest is the
    /// reviewer-side half — the author still has to be someone with no request for change on the
    /// pull request at all.
    /// </summary>
    [Fact]
    public void A_thread_with_no_observed_text_rests_entirely_on_the_reviewer_side_test()
    {
        AdvisoryReviewThreads.AsksSomething("").Should().BeFalse();

        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "teammate", "url", ""),
            new PullRequestReviewer("teammate", ReviewerKind.Human, null, "APPROVED", "head")))
            .Should().ContainSingle(
                "a reviewer who approved and wrote nothing readable is asking nothing")
            .Which.Should().Be("PRRT_1");
    }

    [Fact]
    public void A_thread_whose_author_is_not_a_listed_reviewer_is_never_advisory() =>
        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "someone", "url", "Nice."),
            reviewer: null))
            .Should().BeEmpty(
                "the pull request's own author is filtered out of the reviewer list, so an unmatched "
                + "opener means their standing is unknown rather than approving");

    [Fact]
    public void A_reviewer_who_asked_for_a_change_in_their_review_body_makes_their_thread_dispatch() =>
        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "teammate", "url", "Nice."),
            new PullRequestReviewer("teammate", ReviewerKind.Human, null, null, null)
            {
                LatestReviewBody = "Looks good overall, but please rename the column before this merges.",
            }))
            .Should().BeEmpty(
                "a plain COMMENT review that asks for a change is a request-for-change signal, whatever "
                + "its verdict field says");

    [Fact]
    public void A_comment_only_reviewer_who_asked_nothing_leaves_their_remark_advisory() =>
        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "teammate", "url", "Nice, copying this."),
            new PullRequestReviewer("teammate", ReviewerKind.Human) { LatestReviewBody = "Reading along." }))
            .Should().Equal("PRRT_1");

    private static PullRequestSnapshot Snapshot(UnresolvedHumanThread thread, PullRequestReviewer? reviewer) =>
        new(
            IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 1,
            UnresolvedHumanThreadCount: 1, Reviewers: reviewer is null ? [] : [reviewer], ErroredReview: null,
            CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0)
        {
            UnresolvedReviewThreadIds = [thread.ThreadId],
            UnresolvedHumanThreadIds = [thread.ThreadId],
            UnresolvedHumanThreadDetails = [thread],
        };
}
