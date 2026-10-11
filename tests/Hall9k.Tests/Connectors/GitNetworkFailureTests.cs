using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Which of git's own fetch errors read as a passing network failure a retry can outlast. Pure
/// text in, verdict out: nothing here runs git or touches the network.
/// </summary>
public sealed class GitNetworkFailureTests
{
    [Fact]
    public void The_error_that_failed_a_launch_in_arx_platform_is_transient()
    {
        GitNetworkFailure.IsTransient(
            "fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("could not resolve host")]
    [InlineData("failed to connect to")]
    [InlineData("connection timed out")]
    [InlineData("operation timed out")]
    [InlineData("connection refused")]
    [InlineData("connection reset")]
    [InlineData("connection was reset")]
    [InlineData("network is unreachable")]
    public void Each_named_phrase_is_transient_in_any_letter_case(string phrase)
    {
        GitNetworkFailure.IsTransient($"fatal: unable to access 'https://github.com/x/y.git/': {phrase}")
            .Should().BeTrue();
        GitNetworkFailure.IsTransient(phrase.ToUpperInvariant()).Should().BeTrue();
        GitNetworkFailure.IsTransient($"fatal: {char.ToUpperInvariant(phrase[0])}{phrase[1..]} by peer")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("fatal: couldn't find remote ref refs/pull/9/head")]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/x/y.git/'")]
    [InlineData("ERROR: Repository not found.\nfatal: Could not read from remote repository.")]
    [InlineData("")]
    [InlineData(null)]
    public void A_refusal_waiting_will_not_change_is_not_transient(string? gitError)
    {
        GitNetworkFailure.IsTransient(gitError).Should().BeFalse();
    }
}
