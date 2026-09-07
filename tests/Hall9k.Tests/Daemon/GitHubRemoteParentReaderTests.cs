using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Reading a stacked parent's pull request off recorded <c>gh</c> output (task: a stacked child can
/// stand on a pull request another install owns). The one distinction everything downstream rests
/// on is the one this proves: a pull request the repository does not have is a different fact from
/// a look that could not be made, and collapsing the two would either park a child on a network
/// blip or keep one waiting on a number that will never resolve.
/// </summary>
public sealed class GitHubRemoteParentReaderTests
{
    private const int ParentNumber = 264;

    [Fact]
    public void An_open_pull_request_reads_as_open_with_its_head_branch()
    {
        RemoteParentRead read = GitHubRemoteParentReader.Parse(ParentNumber, """
            {
              "state": "OPEN",
              "headRefName": "feature/teammate-slice",
              "headRefOid": "5f1c0b9a2d3e4f5061728394a5b6c7d8e9f00112",
              "baseRefName": "main",
              "url": "https://github.com/Hallmanac/hall9k/pull/264",
              "closingIssuesReferences": []
            }
            """);

        read.State.Should().Be(RemoteParentState.Open);
        read.HeadBranch.Should().Be("feature/teammate-slice");
        read.HeadCommit.Should().Be("5f1c0b9a2d3e4f5061728394a5b6c7d8e9f00112");
        read.BaseBranch.Should().Be("main");
        read.LinkedWorkItem.Should().BeNull("this pull request names no issue, which is an ordinary shape");
        read.Detail.Should().Contain("feature/teammate-slice");
    }

    [Theory]
    [InlineData("MERGED", "Merged")]
    [InlineData("CLOSED", "ClosedUnmerged")]
    public void The_providers_own_state_word_is_mapped_and_never_inferred(string reported, string expected)
    {
        RemoteParentRead read = GitHubRemoteParentReader.Parse(
            ParentNumber, $$"""{"state": "{{reported}}", "headRefName": "b", "baseRefName": "main"}""");

        read.State.Should().Be((RemoteParentState)expected);
    }

    /// <summary>
    /// A state word this build does not recognise is not an observation. Forcing it into one of the
    /// three would let an unrecognised answer release a child or park one.
    /// </summary>
    [Fact]
    public void A_state_word_this_build_does_not_know_reads_as_unobserved()
    {
        RemoteParentRead read = GitHubRemoteParentReader.Parse(
            ParentNumber, """{"state": "DRAFTED_SOMEHOW", "headRefName": "b", "baseRefName": "main"}""");

        read.State.Should().Be(RemoteParentState.Unknown);
        read.State.WasObserved.Should().BeFalse();
        read.Detail.Should().Contain("does not recognise");
    }

    /// <summary>
    /// The issue the pull request itself says it closes, in the canonical form an
    /// <see cref="ExternalReference"/> uses — so a local task that adopted the same issue matches on
    /// equal terms rather than on a bare number that would collide across repositories.
    /// </summary>
    [Fact]
    public void A_linked_issue_is_read_in_the_canonical_external_reference_form()
    {
        RemoteParentRead read = GitHubRemoteParentReader.Parse(ParentNumber, """
            {
              "state": "OPEN",
              "headRefName": "feature/teammate-slice",
              "baseRefName": "main",
              "url": "https://github.com/Hallmanac/hall9k/pull/264",
              "closingIssuesReferences": [
                { "number": 82, "url": "https://github.com/Hallmanac/hall9k/issues/82" }
              ]
            }
            """);

        read.LinkedWorkItem.Should().Be(new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#82"));
    }

    /// <summary>
    /// gh reported the reference without a usable URL. The number is real and the repository is
    /// not, so the repository is completed from the pull request's own URL — the same one by
    /// construction — rather than a half-formed reference that would match nothing.
    /// </summary>
    [Fact]
    public void A_linked_issue_with_no_url_takes_the_pull_requests_own_repository()
    {
        RemoteParentRead read = GitHubRemoteParentReader.Parse(ParentNumber, """
            {
              "state": "OPEN",
              "headRefName": "b",
              "baseRefName": "main",
              "url": "https://github.com/Hallmanac/hall9k/pull/264",
              "closingIssuesReferences": [ { "number": 82 } ]
            }
            """);

        read.LinkedWorkItem.Should().Be(new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#82"));
    }

    [Fact]
    public async Task A_pull_request_the_repository_does_not_have_reads_as_absent()
    {
        RemoteParentRead read = await new GitHubRemoteParentReader(
                RecordingProcessRunner.Failing(
                    "GraphQL: Could not resolve to a PullRequest with the number of 264. (repository.pullRequest)")
                    .Runner)
            .ReadAsync("/tmp/repo", ParentNumber, CancellationToken.None);

        read.State.Should().Be(RemoteParentState.Absent);
        read.State.WasObserved.Should().BeTrue("gh answered, and what it answered is that there is no such thing");
        read.Detail.Should().Contain($"#{ParentNumber}");
    }

    /// <summary>
    /// Every other failure is a read that could not be made. Reading one as absent would let a
    /// network blip look like a teammate who never opened the pull request — and, at a checkpoint,
    /// park a run on a claim nobody observed (AGENTS.md's never-guess rule).
    /// </summary>
    [Fact]
    public async Task A_look_that_could_not_be_made_reads_as_unobserved()
    {
        RemoteParentRead read = await new GitHubRemoteParentReader(
                RecordingProcessRunner.Failing("dial tcp: lookup api.github.com: no such host").Runner)
            .ReadAsync("/tmp/repo", ParentNumber, CancellationToken.None);

        read.State.Should().Be(RemoteParentState.Unknown);
        read.Detail.Should().Contain("no such host");
    }

    [Fact]
    public async Task An_answer_that_is_not_json_reads_as_unobserved_rather_than_throwing()
    {
        RemoteParentRead read = await new GitHubRemoteParentReader(
                RecordingProcessRunner.Succeeding("not json at all").Runner)
            .ReadAsync("/tmp/repo", ParentNumber, CancellationToken.None);

        read.State.Should().Be(RemoteParentState.Unknown);
        read.Detail.Should().Contain("could not be read as JSON");
    }
}
