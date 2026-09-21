using Hall9k.Domain.Features.Tasks.Documents;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// Refreshes this node's lease heartbeats on a timer. Heartbeating is the daemon's job,
/// not the agents' — agents know nothing about leases (Decisions Log #7). Renewals are
/// document writes, never events, and specifically patches rather than upserts
/// (<see cref="QueueRefreshAsync"/> says why that distinction matters).
/// </summary>
public sealed class LeaseHeartbeatService(
    IDocumentStore store,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<LeaseHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.HeartbeatInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            try
            {
                await using IDocumentSession session = store.LightweightSession();
                int refreshed = await QueueRefreshAsync(session, node.NodeId, DateTimeOffset.UtcNow, stoppingToken);
                if (refreshed > 0)
                {
                    await session.SaveChangesAsync(stoppingToken);
                    logger.LogDebug("Heartbeat refresh issued for {Count} lease(s)", refreshed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Heartbeat cycle failed; will retry next tick");
            }
        }
    }

    /// <summary>
    /// Queues one tick's refresh into the caller's own session, against the caller's own clock, and
    /// returns how many leases it queued one for. The COMMIT is left to the caller, which is what
    /// lets a test open the exact window this method exists to be safe in: query here, let another
    /// writer delete a lease, then commit.
    /// <para>
    /// Each lease is refreshed by PATCH — one <c>update ... where id = ?</c> touching only
    /// <see cref="TaskLease.HeartbeatAt"/> — and never by storing the whole document back.
    /// Storing it back is an UPSERT, which RE-INSERTS a lease another writer deleted between this
    /// tick's query and its commit, and a resurrected lease is a concurrency slot leaked for as
    /// long as the daemon runs: it counts against the run ceiling, and the sweep's own
    /// stale-lease reclaim never fires on it, because this service keeps refreshing it inside the
    /// timeout every tick. The deletes it races are real and several — the startup repair freeing
    /// a partial replicated stream (<c>HeadlessReplicatedStreamRepair</c>, a hosted service away
    /// and on no shared gate), and every <c>h9k</c> command that drops a claim from its own
    /// process (task abandon, task resolve, task retry, run kill). A patch matches zero rows for a
    /// lease that is gone, in whichever order the two commits land, and it leaves every field this
    /// service does not own alone (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </para>
    /// </summary>
    internal static async Task<int> QueueRefreshAsync(
        IDocumentSession session, Guid nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> leaseIds = await session.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .Select(lease => lease.Id)
            .ToListAsync(cancellationToken);

        if (leaseIds.Count == 0)
        {
            return 0;
        }

        foreach (Guid leaseId in leaseIds)
        {
            session.Patch<TaskLease>(leaseId).Set(lease => lease.HeartbeatAt, now);
        }

        return leaseIds.Count;
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
