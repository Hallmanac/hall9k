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
    /// The bare imperative, which is the shape a nit beside an approval most often takes. No
    /// question mark, no request phrasing, no defect claim — and before this test a thread like
    /// this was classified advisory, so the lap that used to fix it stopped being dispatched
    /// while the unresolved thread went on holding the pre-approved merge (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    [Theory]
    [InlineData("Rename this to `Canonical`.")]
    [InlineData("Add a null check here.")]
    [InlineData("Use the sentinel instead.")]
    [InlineData("Drop the cast.")]
    [InlineData("Seal this.")]
    [InlineData("Don't hardcode the path.")]
    [InlineData("Looks right overall. Move this into its own method.")]
    [InlineData("Two things here:\n- rename the column\n- pin the version")]
    [InlineData("1. Extract the loop body")]
    [InlineData("> keep the sentinel")]
    public void A_bare_imperative_asks_for_the_change_it_states(string body) =>
        AdvisoryReviewThreads.AsksSomething(body).Should().BeTrue();

    /// <summary>
    /// The other half of that widening, and the half that could have turned the whole advisory
    /// exception off: the same verbs used descriptively are not in the imperative position, and a
    /// member access a reviewer quoted is code rather than a sentence starting with a verb.
    /// </summary>
    [Theory]
    [InlineData("We use the sentinel here already, good.")]
    [InlineData("This renames the column, which I'd forgotten about.")]
    [InlineData("Ah, that adds the guard I was looking for.")]
    [InlineData("I see now that thread.Add(x) already covers it.")]
    [InlineData("Neat, config.Update(...) handles the reload for free.")]
    [InlineData("Merge when green.")]
    public void The_same_verbs_used_descriptively_ask_nothing(string body) =>
        AdvisoryReviewThreads.AsksSomething(body).Should().BeFalse();

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
    /// reported as EMPTY asks nothing it can be read to ask, so what keeps this honest is the
    /// reviewer-side half — the author still has to be someone with no request for change on the
    /// pull request at all.
    /// </summary>
    [Fact]
    public void A_thread_the_provider_reported_as_empty_rests_entirely_on_the_reviewer_side_test()
    {
        AdvisoryReviewThreads.AsksSomething("").Should().BeFalse();

        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "teammate", "url", ""),
            new PullRequestReviewer("teammate", ReviewerKind.Human, null, "APPROVED", "head")))
            .Should().ContainSingle(
                "a reviewer who approved and wrote nothing at all is asking nothing")
            .Which.Should().Be("PRRT_1");
    }

    /// <summary>
    /// The case the empty string above must never be read as, and the one this class's own
    /// fail-safe promises: a thread whose opening comment the provider did not report is
    /// unreadable, not silent, so it dispatches even beside an approval. Coalescing the two made
    /// an unreadable thread advisory and suppressed the lap (Copilot, PR #397), and the reviewer
    /// here is the most favourable one there is — approved, with nothing asked in words.
    /// </summary>
    [Fact]
    public void A_thread_whose_opening_comment_the_provider_never_reported_is_never_advisory() =>
        AdvisoryReviewThreads.Advisory(Snapshot(
            new UnresolvedHumanThread("PRRT_1", "teammate", "url", null),
            new PullRequestReviewer("teammate", ReviewerKind.Human, null, "APPROVED", "head")))
            .Should().BeEmpty(
                "an unobserved body is unknown rather than blank, and every judgment here defaults "
                + "to dispatching");

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
