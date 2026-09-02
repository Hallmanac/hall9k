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
                session, task, "https://github.com/x/y/pull/24", Now, cts.Token);
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
                session, task, "https://github.com/x/y/pull/24", Now, cts.Token);
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
                session, task, "https://github.com/other-org/other-repo/pull/24", Now, cts.Token);
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
                session, task, "https://github.com/x/y/pull/24", Now, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "a pr-review task's --pr names the pull request it reviewed, never one of its own");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
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

    private static void SeedProject(IDocumentSession session, Guid projectId)
    {
        var registered = ProjectDecider.Register(
            projectId, Guid.Empty, DomainId.New(), $"resolve-{projectId:N}", "/tmp/resolve-repo",
            new Uri("https://github.com/x/y"), "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
    }
}
