using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The pr-review task type's adoption seam (AGENTS.md "a pull-request-review task type"):
/// which reference forms a human may type for --from-pr, what a pull request maps to, and
/// that it is a distinct provider from an issue's — GitHubWorkItemProvider refuses a pull
/// request outright, so this is the only path that adopts one.
/// </summary>
public sealed class GitHubPullRequestProviderTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    private const string PullRequestJson = """
        {
          "number": 42,
          "title": "Add rate limiting to auth endpoints",
          "body": "Fixes #17.\n\nSee also the design doc.",
          "state": "OPEN",
          "url": "https://github.com/Hallmanac/hall9k/pull/42",
          "baseRefName": "main"
        }
        """;

    [Fact]
    public async Task A_pull_request_maps_to_a_canonical_reference_a_title_and_a_body()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(PullRequestJson);

        ImportedWorkItem imported = await Import(gh, "42");

        imported.Reference.Should().Be(new ExternalReference(WorkItemProvider.GitHubPullRequest, "Hallmanac/hall9k#42"));
        imported.Reference.ToString().Should().Be("github-pr:Hallmanac/hall9k#42");
        imported.Title.Should().Be("Add rate limiting to auth endpoints");
        imported.Body.Should().Contain("Fixes #17");
        imported.Status.Should().Be(WorkItemStatus.Open);
        imported.Url.Should().Be(new Uri("https://github.com/Hallmanac/hall9k/pull/42"));
    }

    [Fact]
    public async Task FetchFactsAsync_reads_the_base_branch_the_worktree_will_diff_against()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(PullRequestJson);

        PullRequestFacts facts = await new GitHubPullRequestProvider(gh.Runner, new FixedClock(ObservedAt))
            .FetchFactsAsync("42", "/repos/hall9k", CancellationToken.None);

        facts.BaseRefName.Should().Be("main");
        facts.Number.Should().Be(42);
        facts.Repository.Should().Be("Hallmanac/hall9k");
        gh.Calls.Single().Arguments.Should().ContainInOrder("pr", "view", "42");
    }

    /// <summary>
    /// Security review idea 6be68ee2, process-injection finding 1: a fork's own head, read from
    /// gh's <c>isCrossRepository</c> field, is what tells <c>ReviewPersonaRegistry.Plan</c> to
    /// skip every persona that can drive the product rather than run against a fork's own code.
    /// </summary>
    [Fact]
    public async Task FetchFactsAsync_reads_whether_the_head_sits_on_a_fork()
    {
        const string forkJson = """
            {
              "number": 42,
              "title": "Add rate limiting to auth endpoints",
              "body": "Fixes #17.",
              "state": "OPEN",
              "url": "https://github.com/Hallmanac/hall9k/pull/42",
              "baseRefName": "main",
              "isCrossRepository": true
            }
            """;
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(forkJson);

        PullRequestFacts facts = await new GitHubPullRequestProvider(gh.Runner, new FixedClock(ObservedAt))
            .FetchFactsAsync("42", "/repos/hall9k", CancellationToken.None);

        facts.IsCrossRepository.Should().BeTrue();
        gh.Calls.Single().Arguments.Should().Contain(argument => argument.Contains("isCrossRepository", StringComparison.Ordinal));
    }

    /// <summary>Never guessed true from an absent field — a member's own PR, the ordinary case, reads false.</summary>
    [Fact]
    public async Task FetchFactsAsync_reads_a_member_s_own_head_as_not_a_fork()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(PullRequestJson);

        PullRequestFacts facts = await new GitHubPullRequestProvider(gh.Runner, new FixedClock(ObservedAt))
            .FetchFactsAsync("42", "/repos/hall9k", CancellationToken.None);

        facts.IsCrossRepository.Should().BeFalse();
    }

    [Theory]
    [InlineData("42")]
    [InlineData("#42")]
    public async Task A_bare_number_reads_the_projects_own_repository(string reference)
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(PullRequestJson);

        await Import(gh, reference);

        (string fileName, IReadOnlyList<string> arguments, string workingDirectory) = gh.Calls.Should().ContainSingle().Subject;
        fileName.Should().Be("gh");
        workingDirectory.Should().Be("/repos/hall9k");
        arguments.Should().ContainInOrder("pr", "view", "42");
        arguments.Should().NotContain("--repo", "a bare number means the project's own repository");
    }

    [Fact]
    public async Task An_issue_url_is_refused_as_not_a_pull_request()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(PullRequestJson);

        Func<Task> import = () => Import(gh, "https://github.com/Hallmanac/hall9k/issues/42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("is an issue, not a pull request");
    }

    [Fact]
    public async Task WebUrl_points_at_the_pull_request_path_not_issues()
    {
        GitHubPullRequestProvider provider = new(RecordingProcessRunner.Succeeding(PullRequestJson).Runner);

        Uri? url = provider.WebUrl(new ExternalReference(WorkItemProvider.GitHubPullRequest, "Hallmanac/hall9k#42"));

        url.Should().Be(new Uri("https://github.com/Hallmanac/hall9k/pull/42"));
    }

    private static async Task<ImportedWorkItem> Import(
        RecordingProcessRunner gh, string reference, string workingDirectory = "/repos/hall9k")
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        return await new GitHubPullRequestProvider(gh.Runner, new FixedClock(ObservedAt)).ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHubPullRequest, reference, workingDirectory),
            cts.Token);
    }
}
