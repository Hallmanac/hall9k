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
}
