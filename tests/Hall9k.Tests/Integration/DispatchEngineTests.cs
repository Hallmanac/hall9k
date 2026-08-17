using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.ProcessManagement;
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
            store, node, new UnixProcessManager(), Options.Create(options),
            NullLogger<DispatchEngine>.Instance);

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

        (await engine.ClaimEligibleAsync(cts.Token)).Should().Be(3, "the cap limits claims");
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Be(0, "capacity is exhausted until something finishes");

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
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Be(1);

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
}
