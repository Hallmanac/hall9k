using Hall9k.Domain.Features.Tasks.Documents;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// Refreshes this node's lease heartbeats on a timer. Heartbeating is the daemon's job,
/// not the agents' — agents know nothing about leases (Decisions Log #7). Renewals are
/// document upserts, never events.
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
                Guid nodeId = node.NodeId;
                IReadOnlyList<TaskLease> leases = await session.Query<TaskLease>()
                    .Where(lease => lease.NodeId == nodeId)
                    .ToListAsync(stoppingToken);

                if (leases.Count == 0)
                {
                    continue;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (TaskLease lease in leases)
                {
                    lease.HeartbeatAt = now;
                    session.Store(lease);
                }

                await session.SaveChangesAsync(stoppingToken);
                logger.LogDebug("Heartbeat refreshed for {Count} lease(s)", leases.Count);
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
