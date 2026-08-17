using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// The claim/sweep/adopt core, extracted from the loop so it tests against a bare store.
/// Startup order is adopt → sweep → claim (Decisions Log #7): reattach to what's still
/// alive before declaring anything abandoned, and only then take new work.
/// </summary>
public sealed class DispatchEngine(
    IDocumentStore store,
    NodeContext node,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<DispatchEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Reattach to runs recorded as ours whose processes may still be alive. Until the
    /// executor lands (S1-07) there are no processes to find, so this only reports.
    /// </summary>
    public Task AdoptOrphansAsync(CancellationToken cancellationToken)
    {
        // S1-07 gives this real work: check RunDetails for live runs on this node,
        // verify PID + start time via processManager, resume tailing stream.jsonl.
        _ = processManager;
        logger.LogInformation("Orphan adoption: nothing to adopt (executor arrives in S1-07)");
        return Task.CompletedTask;
    }

    /// <summary>Requeue claimed tasks whose lease heartbeat has gone silent past the timeout.</summary>
    public async Task<int> SweepExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.LeaseTimeout;
        await using IDocumentSession session = store.LightweightSession();

        IReadOnlyList<TaskLease> expired = await session.Query<TaskLease>()
            .Where(lease => lease.HeartbeatAt < cutoff)
            .ToListAsync(cancellationToken);

        int requeued = 0;
        foreach (TaskLease lease in expired)
        {
            TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                lease.Id, token: cancellationToken);
            if (task is null || task.State != TaskState.Claimed)
            {
                session.Delete<TaskLease>(lease.Id);
                continue;
            }

            session.Events.Append(lease.Id, TaskDecider.Requeue(task, RequeueReason.LeaseExpired, DateTimeOffset.UtcNow));
            session.Delete<TaskLease>(lease.Id);
            requeued++;
            logger.LogWarning(
                "Lease expired on task {TaskId} (generation {Generation}, last heartbeat {HeartbeatAt:u}) — requeued",
                lease.Id, lease.LeaseGeneration, lease.HeartbeatAt);
        }

        await session.SaveChangesAsync(cancellationToken);
        return requeued;
    }

    /// <summary>
    /// Claim queued tasks up to the concurrency cap. The claim is the lock: appends race
    /// on the stream version and the database picks the winner (TASK-MODEL.md §2).
    /// </summary>
    public async Task<int> ClaimEligibleAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        Guid nodeId = node.NodeId;
        int active = await session.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .CountAsync(cancellationToken);
        int capacity = _options.MaxConcurrentRuns - active;
        if (capacity <= 0)
        {
            return 0;
        }

        IReadOnlyList<TaskListItem> queued = await session.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Queued.Value))
            .OrderBy(t => t.AddedAt)
            .Take(capacity)
            .ToListAsync(cancellationToken);

        int claimed = 0;
        foreach (TaskListItem candidate in queued)
        {
            if (await TryClaimAsync(candidate.Id, cancellationToken))
            {
                claimed++;
            }
        }

        return claimed;
    }

    private async Task<bool> TryClaimAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (state is null || task is null || task.State != TaskState.Queued)
        {
            return false;
        }

        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, DateTimeOffset.UtcNow);

        session.Events.Append(taskId, expectedVersion: state.Version + 1, claimed);
        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = node.NodeId,
            LeaseGeneration = claimed.LeaseGeneration,
            HeartbeatAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Lost the claim race for task {TaskId} — another claimant won", taskId);
            return false;
        }

        logger.LogInformation(
            "Claimed task {TaskId} at generation {Generation}, run {RunId}",
            taskId, claimed.LeaseGeneration, runId);
        return true;
    }
}
