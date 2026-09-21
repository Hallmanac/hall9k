using FluentAssertions;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What one heartbeat tick may and may not do to a lease another writer deleted underneath it.
/// This tier, not the unit one: the whole defect is which SQL statement the write turns into
/// against a real Postgres (an upsert re-inserts a deleted row, a patch matches nothing), which no
/// fake can answer for.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LeaseHeartbeatTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, medium. The window is real and
    /// unfenced: <c>HeadlessReplicatedStreamRepair</c> frees an abandoned claim's lease from
    /// <c>DispatchLoop</c> while this service ticks in a hosted service of its own, and every
    /// <c>h9k task abandon</c> / <c>task resolve</c> / <c>task retry</c> / <c>run kill</c> deletes
    /// one from another process entirely. Resurrecting the row would leak exactly the concurrency
    /// slot the repair exists to free, and silently: this service would then keep refreshing the
    /// resurrected lease inside the timeout, so the stale-lease sweep never reclaims it either.
    /// </summary>
    [Fact]
    public async Task A_tick_that_queried_a_lease_before_another_writer_deleted_it_does_not_bring_it_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid nodeId = DomainId.New();
        Guid freedTaskId = DomainId.New();
        Guid keptTaskId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new TaskLease
            {
                Id = freedTaskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = Now,
            });
            seed.Store(new TaskLease
            {
                Id = keptTaskId, NodeId = nodeId, LeaseGeneration = 4, HeartbeatAt = Now,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        // The tick's own session, queried but not yet committed: the exact window the race lives in.
        await using IDocumentSession tick = store.LightweightSession();
        int queued = await LeaseHeartbeatService.QueueRefreshAsync(tick, nodeId, Now.AddMinutes(1), cts.Token);
        queued.Should().Be(2);

        await using (IDocumentSession repair = store.LightweightSession())
        {
            repair.Delete<TaskLease>(freedTaskId);
            await repair.SaveChangesAsync(cts.Token);
        }

        await tick.SaveChangesAsync(cts.Token);

        await using IQuerySession read = store.QuerySession();
        (await read.LoadAsync<TaskLease>(freedTaskId, cts.Token)).Should().BeNull(
            "a lease deleted between this tick's query and its commit stays deleted — refreshing it is an "
            + "update of a row that is gone, never an insert of one that is not");
        TaskLease? kept = await read.LoadAsync<TaskLease>(keptTaskId, cts.Token);
        kept.Should().NotBeNull();
        kept!.HeartbeatAt.Should().Be(Now.AddMinutes(1), "the leases still held are refreshed as they always were");
        kept.LeaseGeneration.Should().Be(4, "a refresh writes the heartbeat and nothing else");
    }
}
