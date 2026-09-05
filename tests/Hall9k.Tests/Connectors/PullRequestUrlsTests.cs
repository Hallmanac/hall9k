using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

public sealed class PullRequestUrlsTests
{
    [Theory]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11/")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11?foo=bar")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11#issuecomment-1")]
    [InlineData("https://github.com/Hallmanac/hall9k/pull/11/?foo=bar#top")]
    public void The_number_survives_human_pasted_url_noise(string url) =>
        PullRequestUrls.ParseNumber(url).Should().Be(11);

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://github.com/Hallmanac/hall9k/pulls")]
    [InlineData("https://github.com/")]
    // A GitHub issue is not a pull request: even though its URL ends in a number, the segment
    // right before it is "issues", not "pull", so this must not be read as a pull request number
    // (adversarial review, cycle 1 — a mistyped --pr naming an issue must not silently become a
    // run's merge signal).
    [InlineData("https://github.com/Hallmanac/hall9k/issues/24")]
    public void Anything_without_a_number_yields_zero_never_a_guess(string url) =>
        PullRequestUrls.ParseNumber(url).Should().Be(0);

    [Fact]
    public void NamesForeignRepository_is_false_for_the_projects_own_repository() =>
        PullRequestUrls.NamesForeignRepository(
                "https://github.com/x/y/pull/24", new Uri("https://github.com/x/y"))
            .Should().BeFalse();

    [Fact]
    public void NamesForeignRepository_is_true_for_a_different_owner_or_repo() =>
        PullRequestUrls.NamesForeignRepository(
                "https://github.com/other-org/other-repo/pull/24", new Uri("https://github.com/x/y"))
            .Should().BeTrue();

    [Fact]
    public void NamesForeignRepository_is_true_for_the_same_owner_and_repo_on_a_different_host() =>
        // RepositoryFrom reads owner/repo out of path segments only, so a same-owner same-repo URL
        // on a different host would otherwise slip past the guard undetected (the same host check
        // IsSafePullRequestUrl already carries).
        PullRequestUrls.NamesForeignRepository(
                "https://gitlab.com/x/y/pull/24", new Uri("https://github.com/x/y"))
            .Should().BeTrue();

    [Fact]
    public void NamesForeignRepository_is_never_a_mismatch_against_an_unknown_project_repository() =>
        // A courtesy check, not a hard requirement: a project whose repository cannot be resolved
        // at all must not block a URL over information the caller does not have.
        PullRequestUrls.NamesForeignRepository("https://github.com/x/y/pull/24", projectRepositoryUrl: null)
            .Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void NamesForeignRepository_is_false_when_there_is_nothing_to_compare(string url) =>
        PullRequestUrls.NamesForeignRepository(url, new Uri("https://github.com/x/y")).Should().BeFalse();

    [Theory]
    // Unlike IsSafePullRequestUrl, this check does not require the URL to parse to a pull request
    // number at all — a commit link or an issue link is still comparable by repository, and a
    // caller that must still display such a URL (TaskResolveCommand's task-stream guard) needs
    // exactly that narrower check.
    [InlineData("https://github.com/x/y/commit/abc1234")]
    [InlineData("https://github.com/x/y/issues/24")]
    public void NamesForeignRepository_is_false_for_a_url_naming_the_projects_own_repository_even_without_a_pull_request_number(
        string url) =>
        PullRequestUrls.NamesForeignRepository(url, new Uri("https://github.com/x/y")).Should().BeFalse();
}
