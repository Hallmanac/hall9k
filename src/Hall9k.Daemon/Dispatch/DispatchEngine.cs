using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
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
public sealed record ClaimedWork(Guid TaskId, Guid RunId, int LeaseGeneration);

public sealed class DispatchEngine(
    IDocumentStore store,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<DispatchEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Requeue claimed tasks whose lease heartbeat has gone silent past the timeout —
    /// except tasks whose current run is parked. Parked means waiting on a human, not
    /// abandoned: the sweep refreshes the lease instead, so heartbeat decay (a stopped
    /// daemon, a laptop asleep past the timeout, a sweep racing the first heartbeat
    /// tick) can never requeue the task out from under the human's worktree. Origin
    /// incident (2026-08-18): a review-parked task WAS requeued by lease expiry —
    /// decision #24's "the park keeps the lease alive" held only while the heartbeat
    /// service ran — and the platform rebuilt the same feature from scratch across
    /// generations 2-4 before gen 5 completed.
    /// </summary>
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

            if (await CurrentRunIsParkedAsync(session, task, cancellationToken))
            {
                lease.HeartbeatAt = DateTimeOffset.UtcNow;
                session.Store(lease);
                logger.LogInformation(
                    "Lease on task {TaskId} expired but its run is parked for a human — lease refreshed, not requeued",
                    lease.Id);
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

    private static async Task<bool> CurrentRunIsParkedAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.CurrentRunId is not { } runId)
        {
            return false;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        // CloseoutParked normally holds a Done, lease-free task, but it is included so
        // the guarantee is a property of "parked", not of one park flavor.
        return run is not null
            && (run.State == RunState.ReviewParked || run.State == RunState.CloseoutParked);
    }

    /// <summary>
    /// Claim queued tasks up to the concurrency cap. The claim is the lock: appends race
    /// on the stream version and the database picks the winner (TASK-MODEL.md §2).
    /// </summary>
    public async Task<IReadOnlyList<ClaimedWork>> ClaimEligibleAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        Guid nodeId = node.NodeId;
        int active = await session.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .CountAsync(cancellationToken);
        int capacity = _options.MaxConcurrentRuns - active;
        if (capacity <= 0)
        {
            return [];
        }

        IReadOnlyList<TaskListItem> queued = await session.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Queued.Value))
            .OrderBy(t => t.AddedAt)
            .Take(capacity)
            .ToListAsync(cancellationToken);

        List<ClaimedWork> claimed = [];
        foreach (TaskListItem candidate in queued)
        {
            if (await TryClaimAsync(candidate.Id, cancellationToken) is { } work)
            {
                claimed.Add(work);
            }
        }

        return claimed;
    }

    private async Task<ClaimedWork?> TryClaimAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (state is null || task is null || task.State != TaskState.Queued)
        {
            return null;
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
            return null;
        }

        logger.LogInformation(
            "Claimed task {TaskId} at generation {Generation}, run {RunId}",
            taskId, claimed.LeaseGeneration, runId);
        return new ClaimedWork(taskId, runId, claimed.LeaseGeneration);
    }
}
