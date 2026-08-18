using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public sealed class DispatchEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cap_sweep_and_reclaim_walk_the_full_lease_lifecycle()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        DaemonOptions options = new() { MaxConcurrentRuns = 3, LeaseTimeout = TimeSpan.FromSeconds(60) };
        DispatchEngine engine = new(
            store, node, Options.Create(options), NullLogger<DispatchEngine>.Instance);

        // Five queued tasks, cap of three.
        await using (IDocumentSession seed = store.LightweightSession())
        {
            for (int i = 0; i < 5; i++)
            {
                Guid id = DomainId.New();
                seed.Events.StartStream<TaskAggregate>(id, TaskDecider.Add(
                    id, DomainId.New(), $"Task {i}", ["done"], TaskType.Chore,
                    null, null, null, Now.AddSeconds(i), node.OwnerId));
            }

            await seed.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Should().HaveCount(3, "the cap limits claims");
        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("capacity is exhausted until something finishes");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.Query<TaskLease>().ToListAsync(cts.Token)).Should().HaveCount(3);
        }

        // Expire one lease (heartbeat long gone) — the sweep requeues exactly that task.
        Guid expiredTaskId;
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskLease victim = (await session.Query<TaskLease>().ToListAsync(cts.Token))[0];
            expiredTaskId = victim.Id;
            victim.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            session.Store(victim);
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.SweepExpiredLeasesAsync(cts.Token)).Should().Be(1);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem requeued = (await query.LoadAsync<TaskListItem>(expiredTaskId, cts.Token))!;
            requeued.State.Value.Should().Be("Queued");
            requeued.ClaimedByNodeId.Should().BeNull();
        }

        // Freed capacity: the next cycle claims again, and the requeued task's next claim
        // carries generation 2 — the fencing token moved on (log #7).
        (await engine.ClaimEligibleAsync(cts.Token)).Should().HaveCount(1);

        await using (IQuerySession query = store.QuerySession())
        {
            IReadOnlyList<TaskLease> leases = await query.Query<TaskLease>().ToListAsync(cts.Token);
            leases.Should().HaveCount(3, "back at the cap");

            TaskListItem reclaimed = (await query.LoadAsync<TaskListItem>(expiredTaskId, cts.Token))!;
            if (reclaimed.State.Value == "Claimed")
            {
                reclaimed.LeaseGeneration.Should().Be(2);
            }
        }
    }

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

        // A generous cap: this class shares one database, so other tests' queued work and
        // leases may be present — the assertions below target this task alone.
        DaemonOptions options = new() { MaxConcurrentRuns = 50, LeaseTimeout = TimeSpan.FromSeconds(60) };
        DispatchEngine engine = new(
            store, node, Options.Create(options), NullLogger<DispatchEngine>.Instance);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Retry walk", ["done"], TaskType.Chore,
                null, null, null, Now, node.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<ClaimedWork> first = await engine.ClaimEligibleAsync(cts.Token);
        ClaimedWork firstClaim = first.Should().ContainSingle(w => w.TaskId == taskId).Subject;
        firstClaim.LeaseGeneration.Should().Be(1);

        // The run dies at the push step: task Failed, lease released — the origin incident.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Fail(
                task, firstClaim.RunId, "Push rejected: branch was rebased.", DateTimeOffset.UtcNow));
            session.Delete<TaskLease>(taskId);
            await session.SaveChangesAsync(cts.Token);
        }

        // The human retries; the stream keeps the failure and the queue picks the task up.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Retry(
                task, firstClaim.RunId, "task/abc12345-retry-walk",
                "Push bug fixed; the work is intact.", DateTimeOffset.UtcNow, node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<ClaimedWork> second = await engine.ClaimEligibleAsync(cts.Token);
        ClaimedWork secondClaim = second.Should().ContainSingle(w => w.TaskId == taskId).Subject;
        secondClaim.LeaseGeneration.Should().Be(2, "the fencing token moves on retry claims like any other (log #7)");

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.State.Value.Should().Be("Claimed");
            details.FailureReason.Should().Be("Push rejected: branch was rebased.", "retry does not erase why it failed");
            details.RetryReason.Should().Be("Push bug fixed; the work is intact.");
            details.RetryBranch.Should().Be("task/abc12345-retry-walk", "the launcher resumes it when it survives");
        }
    }
}
