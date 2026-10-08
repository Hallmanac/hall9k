using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The single-review read behind <c>h9k pr reply --review</c> (task: a dispatched session never
/// speaks to a person at the top level of a pull request on its own), and the bot-versus-person rule
/// it shares with the daemon's closeout inspector. The rule decides whether a decline may post, so
/// every shape of author GitHub can return is pinned here.
/// </summary>
public sealed class GitHubPullRequestReviewsTests
{
    private const string PullRequestUrl = "https://github.com/x/y/pull/2042";

    private const string ReviewUrl = PullRequestUrl + "#pullrequestreview-345";

    [Theory]
    [InlineData("User", "jsmotherman", false)]
    [InlineData("Bot", "dependabot[bot]", true)]
    [InlineData("User", "copilot-pull-request-reviewer", true)]
    [InlineData("User", "Copilot", true)]
    [InlineData("Bot", "Copilot", true)]
    [InlineData("Mannequin", "imported-person", false)]
    [InlineData("EnterpriseUserAccount", "corp-person", false)]
    [InlineData("", "a-person-with-no-type", false)]
    [InlineData("User", "mycopilot", false)]
    public void The_author_kind_is_the_closeout_inspectors_own_rule(string type, string login, bool bot)
    {
        PullRequestReview review = GitHubPullRequestReviews.Parse(
            Json(login, type), "x/y", 2042, ReviewUrl);

        review.AuthoredByBot.Should().Be(bot);
        review.AuthorLogin.Should().Be(login);
        review.Url.Should().Be(ReviewUrl, "the url GitHub reported, not the one the caller asked with");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_review_with_no_readable_author_is_a_persons(string? login)
    {
        string json = JsonSerializer.Serialize(new
        {
            html_url = ReviewUrl,
            pull_request_url = "https://api.github.com/repos/x/y/pulls/2042",
            user = login is null ? null : new { login, type = "Bot" },
        });

        PullRequestReview review = GitHubPullRequestReviews.Parse(json, "x/y", 2042, ReviewUrl);

        review.AuthoredByBot.Should().BeFalse();
        review.AuthorLogin.Should().BeNull();
    }

    [Fact]
    public void A_review_github_places_on_another_pull_request_is_refused()
    {
        string json = JsonSerializer.Serialize(new
        {
            html_url = ReviewUrl,
            pull_request_url = "https://api.github.com/repos/x/y/pulls/99",
            user = new { login = "jsmotherman", type = "User" },
        });

        Action act = () => GitHubPullRequestReviews.Parse(json, "x/y", 2042, ReviewUrl);

        act.Should().Throw<DomainValidationException>().WithMessage("*did not place review*");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pull_request_url\":\"https://api.github.com/repos/x/y/pulls/2042\"}")]
    public void An_answer_that_is_not_a_readable_review_is_refused(string json)
    {
        Action act = () => GitHubPullRequestReviews.Parse(json, "x/y", 2042, ReviewUrl);

        act.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData("https://github.com/x/y/pull/2042#pullrequestreview-345", 345)]
    [InlineData("https://GitHub.com/X/Y/pull/2042#pullrequestreview-9", 9)]
    [InlineData("https://github.com/x/y/pull/2042/#pullrequestreview-9", 9)]
    public void A_review_url_on_the_tasks_own_pull_request_names_its_numeric_id(string url, long id)
    {
        (string repository, int number, long reviewId) =
            GitHubPullRequestReviews.ParseReviewTarget(PullRequestUrl, url);

        repository.ToLowerInvariant().Should().Be("x/y");
        number.Should().Be(2042);
        reviewId.Should().Be(id);
    }

    [Theory]
    [InlineData("https://github.com/x/y/pull/99#pullrequestreview-345")]
    [InlineData("https://github.com/x/z/pull/2042#pullrequestreview-345")]
    [InlineData("https://example.com/x/y/pull/2042#pullrequestreview-345")]
    [InlineData("https://github.com/x/y/pull/2042#discussion_r9")]
    [InlineData("https://github.com/x/y/pull/2042")]
    [InlineData("https://github.com/x/y/issues/2042#pullrequestreview-345")]
    [InlineData("pullrequestreview-345")]
    [InlineData("")]
    public void A_url_that_is_not_a_review_on_the_tasks_own_pull_request_is_refused(string url)
    {
        Action act = () => GitHubPullRequestReviews.ParseReviewTarget(PullRequestUrl, url);

        act.Should().Throw<DomainValidationException>().WithMessage("*Nothing was posted*");
    }

    private static string Json(string login, string type) => JsonSerializer.Serialize(new
    {
        html_url = ReviewUrl,
        pull_request_url = "https://api.github.com/repos/x/y/pulls/2042",
        user = new { login, type },
    });
}
