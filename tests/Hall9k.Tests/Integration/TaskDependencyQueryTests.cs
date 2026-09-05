using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Pins <see cref="TaskDependencyQuery"/>'s own read of <see cref="RunDetails.PullRequestNumber"/>
/// and <see cref="RunDetails.FailureReason"/> against a real store, at the layer the orphan-sweep
/// fix actually lives in (independent pre-PR review, cycle 1: the unit tests in
/// <c>TaskDependencyClosureTests</c> construct <see cref="TaskDependency"/> by hand and so never
/// exercise the Marten <c>Select</c> projection this query runs — a regression there, such as a
/// member-mapping change that silently materialized either scalar as null, would pass every one
/// of them while every dependency went back to reading dead exactly as before the fix).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskDependencyQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/7";

    /// <summary>
    /// Mirrors what <c>h9k task resolve --pr</c> appends: TaskFailed then TaskResolved on the
    /// task stream, RunFailed then PullRequestRecordedOnFailedRun on the run stream. The orphan
    /// sweep is still watching this pull request, so the dependency must read alive.
    /// </summary>
    [Fact]
    public async Task A_done_blocker_the_orphan_sweep_still_watches_reads_alive_through_the_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();

        Guid blockerId = await SeedResolvedFailedBlockerAsync(
            store, ownerId, recordPullRequestOnRun: true, closedWithoutMerge: false, cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskDependency> dependencies =
            await TaskDependencyQuery.LoadAsync(query, [blockerId], cts.Token);

        dependencies.Should().ContainSingle();
        dependencies[0].IsDead.Should().BeFalse(
            "the run's own PullRequestNumber, read off RunDetails, still puts it in the orphan "
            + "sweep's candidate set");
    }

    /// <summary>
    /// The sweep's own exclusion, read through the same store: a run the monitor already
    /// observed closed without merging carries <see cref="RunDetails.PullRequestClosedWithoutMerge"/>
    /// as its FailureReason, and the dependency must read dead rather than still watched.
    /// </summary>
    [Fact]
    public async Task A_done_blocker_whose_pull_request_closed_without_merging_reads_dead_through_the_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();

        Guid blockerId = await SeedResolvedFailedBlockerAsync(
            store, ownerId, recordPullRequestOnRun: true, closedWithoutMerge: true, cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskDependency> dependencies =
            await TaskDependencyQuery.LoadAsync(query, [blockerId], cts.Token);

        dependencies.Should().ContainSingle();
        dependencies[0].IsDead.Should().BeTrue(
            "the run's own FailureReason, read off RunDetails, already excludes it from the "
            + "orphan sweep's candidate set");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<Guid> SeedResolvedFailedBlockerAsync(
        DocumentStore store, Guid ownerId, bool recordPullRequestOnRun, bool closedWithoutMerge,
        CancellationToken cancellationToken)
    {
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                blockerId, DomainId.New(), "Ship the schema", ["merged"], TaskType.Chore,
                null, null, null, Now, ownerId),
            ownerId, Now);
        List<object> taskEvents = [.. lifecycle];

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", Now);
        task.Apply(failed);
        taskEvents.Add(failed);

        Hall9k.Domain.Features.Tasks.Events.TaskResolved resolved = TaskDecider.Resolve(
            task, "the work merged; only the gate failed", PullRequestUrl, Now, ownerId);
        task.Apply(resolved);
        taskEvents.Add(resolved);

        session.Events.StartStream<TaskAggregate>(blockerId, [.. taskEvents]);

        List<object> runEvents =
        [
            new RunDispatched(
                runId, blockerId, DomainId.New(), ownerId, 1, DomainId.New(),
                "/tmp/worktree", "task/ship-the-schema", ExecutorMode.Subscription, Now),
            new RunFailed(runId, "the gates never went green", Now),
        ];
        if (recordPullRequestOnRun)
        {
            runEvents.Add(new PullRequestRecordedOnFailedRun(runId, PullRequestUrl, 7, Now));
        }

        if (closedWithoutMerge)
        {
            runEvents.Add(new PullRequestClosed(runId, Now, Now));
        }

        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);

        await session.SaveChangesAsync(cancellationToken);
        return blockerId;
    }
}
