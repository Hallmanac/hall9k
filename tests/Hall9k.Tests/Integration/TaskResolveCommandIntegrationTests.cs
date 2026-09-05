using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="TaskResolveCommand.RecordPullRequestOnRunStreamAsync"/> against a real store —
/// the run-side counterpart to <c>h9k task resolve --pr</c> (backlog: a pull request recorded
/// by h9k task resolve --pr is observed to merge like every other pull request the platform
/// knows about). The defect these tests guard (independent pre-PR review, cycle 1, high): an
/// interactive claim (<c>h9k task work</c>) whose worktree cut fails leaves a Failed task with
/// <see cref="TaskAggregate.CurrentRunId"/> naming a run whose stream was never started
/// (<c>TaskWorkCommand.FailInteractiveClaimAsync</c> appends only to the task stream). Appending
/// unconditionally onto that run id would implicitly create the stream and materialize a stub
/// <c>RunDetails</c> row, which drops the task out of
/// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate set — the one sweep
/// actually built to complete closeout for exactly this shape.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TaskResolveCommandIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failed_task_whose_run_stream_never_started_records_nothing_on_the_run_side()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedInteractiveClaimWithNoRunStreamAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", Now, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream,
            "the run stream was never started, so there is nothing here to append onto");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamStateAsync(runId, cts.Token)).Should().BeNull(
            "appending here must never implicitly create the run stream — that would materialize a stub " +
            "RunDetails row and hide the task from CloseoutEngine's missing-run sweep");
        (await query.LoadAsync<RunDetails>(runId, cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_runs_own_stream_records_the_pull_request_when_it_names_the_projects_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", Now, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.Recorded);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().Be(24);
        run.State.Should().Be(RunState.Failed, "recording the pull request must never move the run off Failed");
    }

    [Fact]
    public async Task A_pull_request_naming_a_different_repository_than_the_project_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", Now,
                new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "a pull request from a repository other than the project's own must never become this run's merge signal");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// A pr-review task's PullRequestUrl names the pull request it reviewed, not one of its own
    /// (adversarial review, cycle 3, high): recording it here would enroll a foreign pull request
    /// as this run's merge signal, letting that pull request's own unrelated merge complete this
    /// task's closeout and run the remote branch-delete cleanup TaskDecider.Reopen already refuses
    /// the type to prevent. This must hold even when the URL names the project's own repository,
    /// which is the ordinary case for a pr-review task (it reviews a pull request in its own
    /// project) — the guard cannot rely on the repository check to catch it.
    /// </summary>
    [Fact]
    public async Task A_pr_review_tasks_failed_run_records_nothing_even_when_the_pull_request_names_the_projects_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token, TaskType.PrReview);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", Now, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "a pr-review task's --pr names the pull request it reviewed, never one of its own");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// A --repo-only project (no --repo-url, so ProjectDetails.RepositoryUrl stays null forever)
    /// falls back to the same ambient `gh repo view` observation RunLauncher and TaskPublishCommand
    /// already use for the identical shape (independent pre-PR review, cycle 3, medium): an earlier
    /// version of this guard read only RepositoryUrl and proceeded — treated the URL as safe —
    /// whenever that was null, which is exactly what --repo-only registration leaves forever.
    /// </summary>
    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_rejects_a_foreign_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/other-org/other-repo/pull/24", cts.Token, gh.Runner);
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", Now, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "gh observed the project's real repository, and the --pr URL names a different one");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// The routed defect this fix closes (independent pre-PR review, cycle 2, adversarial, medium):
    /// <see cref="TaskResolveCommand.ResolveProjectRepositoryUrlAsync"/> used to short-circuit on
    /// <see cref="Hall9k.Connectors.WorkItems.PullRequestUrls.ParseNumber"/> returning zero or less,
    /// which is exactly what a non-pull-request-shaped URL (a commit link) does — so it never
    /// resolved the project's repository at all for this shape, and
    /// <see cref="TaskResolveCommand.SafeTaskStreamPullRequestUrl"/>'s own
    /// <see cref="Hall9k.Connectors.WorkItems.PullRequestUrls.NamesForeignRepository"/> check, fed a
    /// null project repository, treated a foreign commit link as "no mismatch" and let it reach the
    /// task stream verbatim. Exercised through the full pipeline exactly as <c>ExecuteAsync</c> calls
    /// it (<c>ResolveProjectRepositoryUrlAsync</c> then <c>SafeTaskStreamPullRequestUrl</c>), not with
    /// an explicit <c>Uri</c> handed to <c>SafeTaskStreamPullRequestUrl</c> directly, since that
    /// shortcut is exactly what let the defect through the unit tier undetected.
    /// </summary>
    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_rejects_a_foreign_commit_link()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");
        const string foreignCommitLink = "https://github.com/other-org/other-repo/commit/deadbeef";

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, foreignCommitLink, cts.Token, gh.Runner);

        projectRepositoryUrl.Should().Be(new Uri("https://github.com/x/y"),
            "the project's repository must still be resolved through gh for a URL that is not " +
            "pull-request-shaped, since SafeTaskStreamPullRequestUrl's own repository-mismatch check " +
            "applies to every --pr shape, not only ones that parse to a pull request number");

        TaskResolveCommand.RunStreamPullRequestOutcome runStreamOutcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, foreignCommitLink, Now, projectRepositoryUrl, cts.Token);
        string? taskStreamPullRequestUrl = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, foreignCommitLink, runStreamOutcome, projectRepositoryUrl);
        await session.SaveChangesAsync(cts.Token);

        taskStreamPullRequestUrl.Should().BeNull(
            "a commit link naming a foreign repository must never reach the task stream, exactly like " +
            "a foreign pull request — the class of defect this whole task exists to close");
    }

    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_accepts_its_own_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/x/y/pull/24", cts.Token, gh.Runner);
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", Now, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.Recorded);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().Be(24);
    }

    /// <summary>
    /// A resolve with no --pr must stay exactly as inert as it was before this guard existed
    /// (independent pre-PR review, cycle 2, medium): with no URL to check safety for, there is
    /// nothing worth resolving the project's repository for at all, so a --repo-only project
    /// (no --repo-url) must never pay ResolveProjectRepositoryUrlAsync's gh fallback just to
    /// discard the answer immediately. Resolved exactly once now (independent pre-PR review,
    /// cycle 1, adversarial, medium), so this guard lives in ResolveProjectRepositoryUrlAsync
    /// itself rather than in each of its two callers.
    /// </summary>
    [Fact]
    public async Task A_resolve_with_no_pull_request_url_never_shells_out_to_gh()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, null, cts.Token, gh.Runner);
        projectRepositoryUrl.Should().BeNull();
        gh.Calls.Should().BeEmpty("no --pr was given, so there is nothing to resolve the repository for");

        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, null, Now, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded);
    }

    /// <summary>
    /// A pr-review task with no <see cref="TaskAggregate.CurrentRunId"/> at all — a Failed task
    /// that never reached a live run, the same shape the class-level doc comment above describes —
    /// pays neither the <c>ProjectDetails</c> load nor the <c>gh</c> fallback for its repository:
    /// both downstream guards discard the answer regardless (independent pre-PR review, cycle 1,
    /// adversarial, low). <see cref="RecordPullRequestOnRunStreamAsync"/> returns
    /// <see cref="TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream"/> before ever touching
    /// it, and <see cref="TaskResolveCommand.SafeTaskStreamPullRequestUrl"/> excludes a pr-review
    /// task's URL outright in exactly that shape.
    /// </summary>
    [Fact]
    public async Task A_pr_review_task_with_no_current_run_never_resolves_a_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();

        TaskAggregate task = SeedQueuedTask(ownerId, TaskType.PrReview).Task;
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/x/y/pull/24", cts.Token, gh.Runner);

        projectRepositoryUrl.Should().BeNull();
        gh.Calls.Should().BeEmpty(
            "a pr-review task with no current run has no downstream guard left that would ever use " +
            "the answer, so resolving it — gh fallback included — is pure waste");
    }

    /// <summary>
    /// The routed defect this method exists to close: with a run stream that DID exist,
    /// <see cref="TaskResolveCommand.RecordPullRequestOnRunStreamAsync"/> refuses to append a
    /// foreign --pr onto the run stream (it comes back <c>NotRecorded</c>), but before this fix the
    /// task stream's own copy was written from <c>settings.PullRequestUrl</c> verbatim in that case —
    /// reasoning only "a run stream exists, so this must already be safe". A task later reopened
    /// through <c>h9k pr resolve</c> would carry that unguarded URL into
    /// <c>RunLauncher.TryCloseOutMergedPullRequestAsync</c>'s own dispatch-time recheck, which parses
    /// only the number and asks <c>gh</c> about it inside the project's own repository — so an
    /// unrelated repository's merged pull request could falsely close this task out.
    /// </summary>
    [Fact]
    public async Task A_foreign_pull_request_whose_run_stream_exists_records_nothing_on_the_task_stream_either()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);
        Uri projectRepositoryUrl = new("https://github.com/x/y");

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome runStreamOutcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", Now, projectRepositoryUrl,
                cts.Token);
        runStreamOutcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "the run stream existed, but the URL names a foreign repository");

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/other-org/other-repo/pull/24", runStreamOutcome, projectRepositoryUrl);

        recorded.Should().BeNull(
            "a run stream existing must never be read as \"this URL is already safe\" — the task " +
            "stream's own guard has to be checked independently, or a later h9k pr resolve could " +
            "watch an unrelated repository's pull request and falsely close this task out");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// Mirrors TaskWorkCommand.ClaimInteractivelyAsync's own shape up to the exact point its
    /// worktree cut can fail: TaskClaimed lands, then TaskWorkCommand.FailInteractiveClaimAsync
    /// appends TaskFailed to the task stream alone — the run stream is never started, because
    /// RunDispatched is only ever appended after the checkout succeeds.
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedInteractiveClaimWithNoRunStreamAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) = SeedQueuedTask(ownerId);

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.ClaimInteractively(task, ownerId, runId, Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "cancelled while preparing the worktree", Now);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        SeedProject(session, projectId);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    /// <summary>
    /// The ordinary shape a headless dispatch leaves a Failed task in: the run stream did start
    /// (RunDispatched), and later failed on its own (RunFailed) — the case
    /// RecordPullRequestOnRunStreamAsync's guard must still append onto.
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedDispatchedRunAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken,
        TaskType? type = null)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) =
            SeedQueuedTask(ownerId, type ?? TaskType.Chore);
        Guid nodeId = DomainId.New();

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", Now);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                "/tmp/resolve-worktree", "task/resolve-branch", ExecutorMode.Subscription, Now),
            new RunFailed(runId, "the gates never went green", Now));
        SeedProject(session, projectId);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    /// <summary>
    /// The same shape as <see cref="SeedFailedDispatchedRunAsync"/>, but registered with --repo
    /// and no --repo-url — the shape ResolveProjectRepositoryUrlAsync's gh fallback exists for
    /// (ProjectDetails.RepositoryUrl stays null forever, since nothing backfills it and there is
    /// no h9k project set --repo-url).
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedDispatchedRunWithoutRepositoryUrlAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) =
            SeedQueuedTask(ownerId, TaskType.Chore);
        Guid nodeId = DomainId.New();

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", Now);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                "/tmp/resolve-worktree", "task/resolve-branch", ExecutorMode.Subscription, Now),
            new RunFailed(runId, "the gates never went green", Now));
        SeedProjectWithRepositoryUrl(session, projectId, repositoryUrl: null);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    private static (Guid TaskId, TaskAggregate Task, List<object> Events, Guid ProjectId) SeedQueuedTask(
        Guid ownerId, TaskType? type = null)
    {
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["merged"], type ?? TaskType.Chore, null, null,
                null, Now, ownerId),
            ownerId, Now);

        return (taskId, task, [.. lifecycle], projectId);
    }

    private static void SeedProject(IDocumentSession session, Guid projectId) =>
        SeedProjectWithRepositoryUrl(session, projectId, new Uri("https://github.com/x/y"));

    private static void SeedProjectWithRepositoryUrl(IDocumentSession session, Guid projectId, Uri? repositoryUrl)
    {
        var registered = ProjectDecider.Register(
            projectId, Guid.Empty, DomainId.New(), $"resolve-{projectId:N}", "/tmp/resolve-repo",
            repositoryUrl, "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
    }
}
