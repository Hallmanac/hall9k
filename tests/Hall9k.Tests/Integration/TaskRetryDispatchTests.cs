using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The retry walk (Decisions Log #25) through the real store and dispatch engine: a
/// failed task is invisible to the claim loop until a human retries it, then flows
/// through the unchanged sweep/claim pipeline with the fencing token moving as usual.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskRetryDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retried_failed_task_is_claimed_again_with_the_next_lease_generation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        DispatchEngine engine = new(
            store, node, Options.Create(new DaemonOptions { MaxConcurrentRuns = 3 }),
            NullLogger<DispatchEngine>.Instance);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Retry walk", ["done"], TaskType.Chore,
                null, null, null, Now, node.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle(w => w.TaskId == taskId);

        // The run fails the task — the shape of every daemon failure path: TaskFailed
        // appended, lease released.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Fail(
                task, task.CurrentRunId!.Value, "Push failed: rejected", DateTimeOffset.UtcNow));
            session.Delete<TaskLease>(taskId);
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("Failed is terminal until a human retries");

        // The human-only exit: h9k task retry appends TaskRetried through the decider.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Retry(
                task, "push bug fixed; the work survives", "task/abc-branch", DateTimeOffset.UtcNow, node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<ClaimedWork> reclaimed = await engine.ClaimEligibleAsync(cts.Token);
        reclaimed.Should().ContainSingle(w => w.TaskId == taskId);
        reclaimed[0].LeaseGeneration.Should().Be(2, "the retry claim moves the fencing token like any other (log #7)");
    }
}
