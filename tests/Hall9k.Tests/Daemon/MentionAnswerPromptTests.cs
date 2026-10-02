using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The mention answer lap's own prompt (decision dce39370): with no report of an earlier review on
/// the page it never says a review already happened, and on the owner's own pull request it says
/// it answers the owner rather than reviews their work.
/// </summary>
public sealed class MentionAnswerPromptTests
{
    private static readonly PullRequestMentionComment Comment = new(
        "IC_abc", "taylor-dennison", "@Hallmanac just curious what the motivating factor is here?",
        "https://github.com/acme/web/pull/7#issuecomment-1", new DateTimeOffset(2026, 10, 1, 15, 49, 0, TimeSpan.Zero));

    private static string Build(string? priorReport, bool ownPullRequest) =>
        MentionFollowUpPromptBuilder.Build(
            "acme/web", 7, "/work/pr-7", "main", Comment, priorReport, QaReviewPromptTests.SomeProject(),
            taggedLogin: "Hallmanac", ownPullRequest: ownPullRequest);

    [Fact]
    public void An_own_pull_request_prompt_says_it_answers_the_owner_and_that_no_review_was_asked_for()
    {
        string prompt = Build(priorReport: null, ownPullRequest: true);

        prompt.Should().Contain("that same login wrote the pull request")
            .And.Contain("It is not a review of the owner's own work");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void A_prompt_with_no_readable_prior_report_never_says_a_review_already_happened(string? priorReport, bool ownPullRequest)
    {
        string prompt = Build(priorReport, ownPullRequest);

        prompt.Should().NotContain("You already reviewed")
            .And.NotContain("Your own review, already delivered")
            .And.NotContain("earlier review already produced")
            .And.NotContain("the review that got you here");
        prompt.Should().Contain("There is no earlier findings report to read here");
    }

    [Fact]
    public void A_prompt_with_a_prior_report_still_opens_as_a_follow_up_to_that_review()
    {
        string prompt = Build(priorReport: "# Pull request review findings", ownPullRequest: false);

        prompt.Should().Contain("You already reviewed this pull request")
            .And.Contain("Your own review, already delivered")
            .And.Contain("# Pull request review findings")
            .And.NotContain("There is no earlier findings report");
    }

    [Fact]
    public void Every_shape_still_quotes_the_comment_and_asks_for_the_drafted_reply()
    {
        string prompt = Build(priorReport: null, ownPullRequest: true);

        prompt.Should().Contain("just curious what the motivating factor is here?")
            .And.Contain("## Drafted reply");
    }
}
