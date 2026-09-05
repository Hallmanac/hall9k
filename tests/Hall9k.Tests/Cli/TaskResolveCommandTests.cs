using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
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

    /// <summary>
    /// <see cref="TaskResolveCommand.SafeTaskStreamPullRequestUrl"/> is the guard the
    /// task-stream side needs unconditionally — called regardless of what
    /// <see cref="TaskResolveCommand.RecordPullRequestOnRunStreamAsync"/> decided for the run stream
    /// (routed defect fix, independent pre-PR review, cycle 1, medium: a run stream existing was
    /// previously read as "this URL is already safe to show on the task", but a run stream can exist
    /// and still come back <c>NotRecorded</c> for a URL naming a foreign repository — and that unsafe
    /// URL used to reach the task stream verbatim in that case). With no run stream at all, nothing
    /// on the run side can ever protect this task from CloseoutEngine's missing-run sweep either,
    /// since no RunDetails row will ever materialize to drop it back out of
    /// TasksWithMissingRunRecordsAsync's own candidate shape, so a pr-review task's --pr is excluded
    /// here too in exactly that shape — the task stream is the only lever left to keep it out of that
    /// candidate set at all. Moved here from the integration tier (independent pre-PR review, cycle
    /// 1, adversarial, low): this method and its sibling tests below are pure, DB-free logic over a
    /// TaskAggregate built in memory, so they belong in the unit tier alongside
    /// BuildFailedRunPullRequestEvent's own tests above, not in a class that requires Docker.
    /// </summary>
    [Fact]
    public void A_pr_review_tasks_missing_run_stream_records_nothing_on_the_task_stream_either()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New(), TaskType.PrReview);

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/x/y/pull/24", TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream,
            new Uri("https://github.com/x/y"));

        recorded.Should().BeNull(
            "a pr-review task's --pr names the pull request it reviewed, and with no run stream to " +
            "protect it, recording it on the task stream would still reach CloseoutEngine's missing-run sweep");
    }

    /// <summary>
    /// The routed-but-since-corrected widening (independent pre-PR review, cycle 1, medium and
    /// adversarial): a pr-review task's --pr still names the pull request it reviewed, not one of
    /// its own, but only *enrollment* in closeout's orphan sweep is excepted for it — this option's
    /// own help text and AGENTS.md both promise it is still recorded and shown by h9k status. With a
    /// run stream already existing, that candidate shape is reachable regardless of whether this URL
    /// is recorded here (the run itself, terminal with no pull-request number, already matches it),
    /// and CloseoutEngine.InspectMissingRunAsync applies the identical pr-review guard at inspection
    /// time before ever treating it as watchable — so recording it here for display is safe.
    /// </summary>
    [Fact]
    public void A_pr_review_tasks_pull_request_still_records_on_the_task_stream_when_a_run_stream_exists()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New(), TaskType.PrReview);

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/x/y/pull/24", TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            new Uri("https://github.com/x/y"));

        recorded.Should().Be("https://github.com/x/y/pull/24",
            "a run stream existing already gives CloseoutEngine.InspectMissingRunAsync's own pr-review " +
            "guard the chance to protect this task, so the URL is safe to record for display here too");
    }

    [Fact]
    public void A_foreign_pull_request_with_no_run_stream_records_nothing_on_the_task_stream_either()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New());

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/other-org/other-repo/pull/24",
            TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream, new Uri("https://github.com/x/y"));

        recorded.Should().BeNull(
            "with no run stream to protect it, recording a foreign pull request on the task stream " +
            "would still reach CloseoutEngine's missing-run sweep");
    }

    [Fact]
    public void An_ordinary_pull_request_with_no_run_stream_still_records_on_the_task_stream()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New());

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/x/y/pull/24", TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream,
            new Uri("https://github.com/x/y"));

        recorded.Should().Be("https://github.com/x/y/pull/24",
            "this is exactly the missing-run sweep's own candidate shape, and the URL is safe: it " +
            "names the project's own repository and this is not a pr-review task");
    }

    /// <summary>
    /// The ordinary headless-dispatch shape every non-pr-review Failed task's own resolve takes: a
    /// valid pull request on the project's own repository was just appended to the run stream
    /// (<c>RunStreamPullRequestOutcome.Recorded</c>), and the task stream's own copy must reach it
    /// too, for h9k task show and closeout's watch alike (independent pre-PR review, cycle 1,
    /// conformance, low: no test exercised this outcome directly, even though it is the case the
    /// whole guard exists to serve — a regression that made SafeTaskStreamPullRequestUrl return null
    /// for Recorded would leave the rest of the suite green).
    /// </summary>
    [Fact]
    public void An_ordinary_pull_request_recorded_on_the_run_stream_also_records_on_the_task_stream()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New());

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/x/y/pull/24", TaskResolveCommand.RunStreamPullRequestOutcome.Recorded,
            new Uri("https://github.com/x/y"));

        recorded.Should().Be("https://github.com/x/y/pull/24");
    }

    /// <summary>
    /// A --pr that is not pull-request-shaped at all (a commit link) must still be recorded for
    /// display, since the option's own help text and AGENTS.md promise --pr "records where the work
    /// landed" unconditionally and condition only *enrollment* on it naming a real pull request
    /// (independent pre-PR review, cycle 1, conformance and adversarial, medium: an earlier version
    /// of this guard called PullRequestUrls.IsSafePullRequestUrl directly, whose own ParseNumber
    /// gate silently dropped display for exactly this shape too — a narrowing neither document
    /// describes). The check that does still apply is the repository match, not the pull-request
    /// shape: a foreign-repository commit link is refused for the same reason a foreign pull
    /// request is.
    /// </summary>
    [Fact]
    public void A_url_that_does_not_parse_to_a_pull_request_still_records_on_the_task_stream_when_the_repository_matches()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New());

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/x/y/commit/abc1234", TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream,
            new Uri("https://github.com/x/y"));

        recorded.Should().Be("https://github.com/x/y/commit/abc1234");
    }

    [Fact]
    public void A_url_that_does_not_parse_to_a_pull_request_still_records_nothing_when_the_repository_is_foreign()
    {
        TaskAggregate task = SeedQueuedTask(DomainId.New());

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/other-org/other-repo/commit/abc1234",
            TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream, new Uri("https://github.com/x/y"));

        recorded.Should().BeNull(
            "a commit link naming a foreign repository is exactly as unsafe as a foreign pull request");
    }

    private static TaskAggregate SeedQueuedTask(Guid ownerId, TaskType? type = null)
    {
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();

        (TaskAggregate task, _) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["merged"], type ?? TaskType.Chore, null, null,
                null, Now, ownerId),
            ownerId, Now);

        return task;
    }
}
