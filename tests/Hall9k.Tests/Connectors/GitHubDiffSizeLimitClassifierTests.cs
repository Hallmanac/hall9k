using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// <see cref="GitHubDiffSizeLimitClassifier"/> is a pure function over gh's own reported error
/// text — no store, no I/O — the same tier <see cref="Hall9k.Daemon.Execution.BudgetExhaustionParser"/>
/// and <see cref="Hall9k.Daemon.Execution.LaunchFailureClassifier"/> are tested at.
/// </summary>
public sealed class GitHubDiffSizeLimitClassifierTests
{
    [Fact]
    public void GitHubs_own_file_count_ceiling_wording_is_recognized()
    {
        GitHubDiffSizeLimitClassifier.IsDiffTooLarge(
            "gh could not read pull request acme/web#1: HTTP 406: Sorry, the diff exceeded the "
            + "maximum number of files (300), so it is not available (https://api.github.com/...)")
            .Should().BeTrue();
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        GitHubDiffSizeLimitClassifier.IsDiffTooLarge("MAXIMUM NUMBER OF FILES exceeded").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gh is not authenticated. Run 'gh auth login' and try again.")]
    [InlineData("GitHub has no pull request acme/web#1. gh reported: no pull requests found.")]
    public void An_unrelated_gh_failure_is_never_misread_as_the_size_ceiling(string? message)
    {
        GitHubDiffSizeLimitClassifier.IsDiffTooLarge(message).Should().BeFalse();
    }
}
