using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskResolveCommand.BuildFailedRunPullRequestEvent"/> decides whether h9k task
/// resolve --pr also records the pull request on the run stream (backlog: a pull request
/// recorded by h9k task resolve --pr is observed to merge like every other pull request the
/// platform knows about). A resolve with no --pr, or a --pr that does not parse to a real
/// pull request number, must build nothing — the run stays exactly as invisible to
/// CloseoutEngine's orphan sweep as it already was.
/// </summary>
public sealed class TaskResolveCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_the_run_event_from_a_parseable_pull_request_url()
    {
        Guid runId = DomainId.New();

        PullRequestRecordedOnFailedRun? recorded = TaskResolveCommand.BuildFailedRunPullRequestEvent(
            runId, "https://github.com/x/y/pull/24", Now);

        recorded.Should().NotBeNull();
        recorded!.Id.Should().Be(runId);
        recorded.PullRequestUrl.Should().Be("https://github.com/x/y/pull/24");
        recorded.PullRequestNumber.Should().Be(24);
        recorded.RecordedAt.Should().Be(Now);
    }

    [Fact]
    public void Builds_nothing_when_no_pull_request_url_is_given()
    {
        TaskResolveCommand.BuildFailedRunPullRequestEvent(DomainId.New(), null, Now).Should().BeNull(
            "a resolve with no --pr must never enter the orphan sweep's candidate set");
    }

    [Fact]
    public void Builds_nothing_for_a_blank_pull_request_url()
    {
        TaskResolveCommand.BuildFailedRunPullRequestEvent(DomainId.New(), "   ", Now).Should().BeNull();
    }

    [Fact]
    public void Builds_nothing_for_an_unparseable_pull_request_url()
    {
        // Never guess a number (AGENTS.md's never-guess rule): a URL that does not resolve to a
        // real pull request number must not fabricate one, so nothing is appended.
        TaskResolveCommand.BuildFailedRunPullRequestEvent(DomainId.New(), "not a url", Now).Should().BeNull();
    }

    [Fact]
    public void Builds_the_run_event_when_the_pull_request_names_the_projects_own_repository()
    {
        Guid runId = DomainId.New();

        PullRequestRecordedOnFailedRun? recorded = TaskResolveCommand.BuildFailedRunPullRequestEvent(
            runId, "https://github.com/x/y/pull/24", Now, new Uri("https://github.com/x/y"));

        recorded.Should().NotBeNull();
        recorded!.PullRequestNumber.Should().Be(24);
    }

    [Fact]
    public void Builds_the_run_event_when_the_project_repository_is_unknown()
    {
        // A best-effort courtesy check, not a hard requirement (the same shape
        // RunLauncher.LaunchAsync's pr-review repository check is): a project with no
        // repository URL recorded must not block a resolve over information this command does
        // not have.
        TaskResolveCommand.BuildFailedRunPullRequestEvent(
            DomainId.New(), "https://github.com/x/y/pull/24", Now, projectRepositoryUrl: null).Should().NotBeNull();
    }

    [Fact]
    public void Builds_nothing_for_a_pull_request_naming_a_different_repository_than_the_project()
    {
        // A --pr URL from a repository other than the project's own must never become this run's
        // merge signal: CloseoutEngine inspects strictly by number against the project's own
        // repository, so a false match would let an unrelated pull request's merge complete this
        // task's closeout and delete this run's own branch out from under it (adversarial
        // review, cycle 1).
        TaskResolveCommand.BuildFailedRunPullRequestEvent(
                DomainId.New(), "https://github.com/other-org/other-repo/pull/24", Now,
                new Uri("https://github.com/x/y"))
            .Should().BeNull();
    }

    [Fact]
    public void Builds_nothing_for_a_pull_request_naming_the_same_owner_and_repo_on_a_different_host()
    {
        // PullRequestUrls.RepositoryFrom reads owner/repo out of path segments only, so a same-owner
        // same-repo URL on a different host would otherwise slip past the guard undetected
        // (adversarial review, cycle 1, medium): https://gitlab.com/x/y/pull/24 must not be treated
        // as the same repository as a project recorded at https://github.com/x/y.
        TaskResolveCommand.BuildFailedRunPullRequestEvent(
                DomainId.New(), "https://gitlab.com/x/y/pull/24", Now,
                new Uri("https://github.com/x/y"))
            .Should().BeNull();
    }
}
