using Hall9k.Daemon.ProcessManagement;
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
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<DispatchEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// When the last sweep started, by this process's wall clock — the baseline for
    /// suspension detection. Sweeps run on the single dispatch loop, never concurrently.
    /// </summary>
    private DateTimeOffset? _lastSweepStartedAt;

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
    /// Leases this node holds get one more defense: the OS is asked whether the run's
    /// process is alive before the timestamp is believed (system sleep masquerades as
    /// node death otherwise — the 2026-08-18 generation storm).
    /// </summary>
    public Task<int> SweepExpiredLeasesAsync(CancellationToken cancellationToken) =>
        SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken);

    /// <summary>
    /// The wall clock is a parameter so tests can drive the suspension detector across a
    /// simulated sleep; the daemon always sweeps at UtcNow via the overload above.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RefreshLocalHeartbeatsAfterSuspensionAsync(now, cancellationToken);

        DateTimeOffset cutoff = now - _options.LeaseTimeout;
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
                lease.HeartbeatAt = now;
                session.Store(lease);
                logger.LogInformation(
                    "Lease on task {TaskId} expired but its run is parked for a human — lease refreshed, not requeued",
                    lease.Id);
                continue;
            }

            if (lease.NodeId == node.NodeId && await LocalRunProcessIsAliveAsync(session, task, cancellationToken))
            {
                lease.HeartbeatAt = now;
                session.Store(lease);
                logger.LogInformation(
                    "Lease on task {TaskId} looks expired by heartbeat, but its agent process is alive on this node — lease refreshed, not requeued",
                    lease.Id);
                continue;
            }

            session.Events.Append(lease.Id, TaskDecider.Requeue(task, RequeueReason.LeaseExpired, now));
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
    /// Wake-from-suspension detection (origin incident, 2026-08-18): a laptop lid-close
    /// froze the whole daemon; each wake ran this sweep before the heartbeat service's
    /// first tick, saw 50-minute-stale local heartbeats, and requeued tasks whose agents
    /// were about to resume — five simultaneous generations on one task. A wall-clock gap
    /// between sweeps far beyond the sweep cadence means the daemon (heartbeat service
    /// included) was suspended — sleep, debugger pause, VM freeze; no platform API needed.
    /// Stale local heartbeats then say nothing about their processes, so they are
    /// refreshed BEFORE expiry is evaluated. Remote leases are untouched: a genuinely
    /// silent remote node still expires on the timeout.
    /// </summary>
    private async Task RefreshLocalHeartbeatsAfterSuspensionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset? previous = _lastSweepStartedAt;
        _lastSweepStartedAt = now;
        TimeSpan expectedGap = _options.PollInterval + _options.HeartbeatInterval;
        if (previous is not { } last || now - last <= expectedGap)
        {
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<TaskLease> local = await session.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .ToListAsync(cancellationToken);
        if (local.Count == 0)
        {
            return;
        }

        foreach (TaskLease lease in local)
        {
            lease.HeartbeatAt = now;
            session.Store(lease);
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Wall clock jumped {Gap} between sweeps (expected ≤ {Expected}) — the daemon was suspended; refreshed {Count} local lease(s) before evaluating expiry",
            now - last, expectedGap, local.Count);
    }

    /// <summary>
    /// The operating system outranks a timestamp for leases this node holds: the run's
    /// recorded pid + start time are a process identity (the adoption path's discipline,
    /// log #2 — a bare pid is a lie waiting to happen), and a live local process means a
    /// live lease no matter how stale the heartbeat reads. Only when the OS cannot be
    /// asked (no pid recorded, or the lease belongs to another node) does the timestamp
    /// decide.
    /// </summary>
    private async Task<bool> LocalRunProcessIsAliveAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.CurrentRunId is not { } runId)
        {
            return false;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        return run is { ProcessId: { } processId, ProcessStartedAt: { } startedAt }
            && run.NodeId == node.NodeId
            && processManager.IsAlive(processId, startedAt);
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
    /// Claim this owner's queued tasks up to the concurrency cap. The claim is the lock:
    /// appends race on the stream version and the database picks the winner (TASK-MODEL.md §2).
    /// Draft, Published and Blocked tasks are structurally invisible here — a task becomes
    /// claimable only through an explicit human assignment (Decisions Log #34).
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

        // The whole claim rule, as one indexed-friendly filter (Decisions Log #34): Queued
        // means a human assigned it and every dependency has closed out, and the owner match
        // means those were this node's owner's decisions. Ordering is untouched — FIFO by
        // AddedAt among the ready set; dependencies and assignment shape that set, not its order.
        Guid ownerId = node.OwnerId;
        IReadOnlyList<TaskListItem> queued = await session.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Queued.Value))
            .Where(t => t.AssignedOwnerId == ownerId)
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
        if (state is null || task is null || task.State != TaskState.Queued || task.AssignedOwnerId != node.OwnerId)
        {
            return null;
        }

        if (await PreviousRunStillRunsHereAsync(session, taskId, cancellationToken))
        {
            logger.LogWarning(
                "Task {TaskId} is queued but a previous run's agent process is still alive on this node — claim refused (single-flight per task per node)",
                taskId);
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

    /// <summary>
    /// Single-flight per task per node — the other half of the sleep defense: even when a
    /// requeue slipped through (the wake-time race won before this build, or an operator
    /// requeued by hand), a fresh claim is refused while a previous generation's agent is
    /// still alive here. The refusal is per cycle: once the OS reports the process gone,
    /// the next claim proceeds. Identity is pid + start time (log #2), never a bare pid.
    /// </summary>
    private async Task<bool> PreviousRunStillRunsHereAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> local = await session.Query<RunDetails>()
            .Where(r => r.TaskId == taskId && r.NodeId == nodeId)
            .Where(r => r.MatchesSql(
                "d.data ->> 'state' in (?, ?)", RunState.Dispatched.Value, RunState.Running.Value))
            .ToListAsync(cancellationToken);

        return local.Any(run => run is { ProcessId: { } processId, ProcessStartedAt: { } startedAt }
            && processManager.IsAlive(processId, startedAt));
    }
}
