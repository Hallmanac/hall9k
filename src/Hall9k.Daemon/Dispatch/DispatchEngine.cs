using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
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
    DaemonConnection connection,
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
    /// Tasks already reported as deferred by the ceiling, so the log states each deferral once
    /// rather than once per sweep — at a five-second cadence, a per-sweep line would bury the
    /// dispatches it sits between. Rebuilt every sweep from the tasks actually passed over, so
    /// a task that gets claimed and later queues behind the ceiling again is announced again.
    /// </summary>
    private readonly HashSet<Guid> _deferredClaims = [];

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

            RunDetails? run = task.CurrentRunId is { } currentRunId
                ? await session.LoadAsync<RunDetails>(currentRunId, cancellationToken)
                : null;

            if (run is not null && (run.State == RunState.ReviewParked || run.State == RunState.CloseoutParked))
            {
                // CloseoutParked normally holds a Done, lease-free task, but it is included so
                // the guarantee is a property of "parked", not of one park flavor.
                lease.HeartbeatAt = now;
                session.Store(lease);
                logger.LogInformation(
                    "Lease on task {TaskId} expired but its run is parked for a human — lease refreshed, not requeued",
                    lease.Id);
                continue;
            }

            if (lease.NodeId == node.NodeId && LocalRunProcessIsAlive(run))
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

            // The run the lease belonged to ends here too, in the same transaction — but only
            // when it is this node's own run that the lease was covering. Dispatched and Running
            // are the two states a process is resident for, and the guard above has already
            // refused to requeue past a live local one, so a local run reaching this line has
            // been asked about and answered for — and a run left reading Running with nothing
            // behind it would go on occupying a concurrency slot until the next daemon start's
            // orphan adoption noticed (Decisions Log #64).
            //
            // Another node's run is left exactly as it is. This sweep sees every stale lease in
            // the database, not only its own, and it cannot ask that machine's operating system
            // anything: a stopped daemon whose detached agent is still working is the platform's
            // ordinary case (down is not death, log #29), and recording RunFailed for it would
            // hide the run from that node's own adoption filter, so the live session is never
            // re-monitored and its work is thrown away. The requeue still happens — the task is
            // the shared thing — and the run stays that node's to conclude.
            //
            // Nothing startup adoption resumes is failed here, and it resumes three things.
            // Verifying and UnderReview are two of them: the build session has already exited by
            // then, so its recorded pid says nothing about whether the run is over. The third is
            // a run still reading Dispatched or Running whose process is gone but whose result is
            // already on disk — adoption re-monitors on alive-or-result-on-disk
            // (RunSupervisor.AdoptOrphansAsync), and the agent that finished while the daemon was
            // down is exactly that case, so the shared check (RunResultFile) is asked here too.
            // On the startup path adoption runs immediately before this sweep, so failing any of
            // the three would record a failure for a pipeline executing at that moment, which the
            // next event that pipeline appends would silently undo. A resumed pipeline is a live
            // session tree and goes on holding its slot, honestly.
            // Origin incident (2026-08-22): pre-PR review of this branch caught the first cut
            // failing a run whose review cycle adoption had just restarted, a second cut failing
            // another node's still-running one, and a third the finished-while-down run above.
            if (lease.NodeId == node.NodeId
                && run is not null
                && run.NodeId == node.NodeId
                && (run.State == RunState.Dispatched || run.State == RunState.Running)
                && !await RunResultFile.AlreadyWrittenAsync(run.Id, cancellationToken))
            {
                session.Events.Append(run.Id, new RunFailed(run.Id, LeaseExpiryFailure(lease, run), now));
            }

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
    private bool LocalRunProcessIsAlive(RunDetails? run) =>
        run is { ProcessId: { } processId, ProcessStartedAt: { } startedAt }
        && run.NodeId == node.NodeId
        && processManager.IsAlive(processId, startedAt);

    /// <summary>
    /// Why the run ended, stated as what the sweep actually observed and no further (AGENTS.md:
    /// never guess at unobserved facts). A full process identity — pid and start time, the pair
    /// LocalRunProcessIsAlive needs — means the operating system was asked and said the session
    /// was gone. Without one there was nothing to ask about, and saying the session "was gone"
    /// would put an observation in the audit trail that nobody made.
    /// </summary>
    private static string LeaseExpiryFailure(TaskLease lease, RunDetails run) =>
        run is { ProcessId: { } processId, ProcessStartedAt: not null }
            ? $"Lease expired at generation {lease.LeaseGeneration}; the agent process (pid {processId}) was gone when the sweep asked this machine."
            : $"Lease expired at generation {lease.LeaseGeneration}; no agent process identity was ever recorded for this run, so there was none to ask about.";

    /// <summary>
    /// The dependency safety net (Decisions Log #34). The closeout monitor unblocks dependents
    /// the moment it observes a merge, but three things only a sweep catches: a merge observed
    /// by a node that has since stopped, a blocker that reached Failed or Abandoned — a death
    /// produces no closeout event to react to — and the recovery of one, since h9k task retry
    /// puts the blocker back to work without touching its dependents either (log #61). Blocked
    /// tasks are waiting rather than working, so the set this walks is small.
    /// </summary>
    public async Task<DependencyReevaluation> ReevaluateBlockedTasksAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        DependencyReevaluation reevaluation = await TaskDependencyResolver.ForEveryBlockedTaskAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);

        // The recorded reason, not a summary of it: a hold is also restated for a blocker that
        // died a different death since, and "failed or was abandoned" would misstate the cause
        // that h9k task show reports for exactly those (a resolved blocker reads Done).
        foreach (DependencyHold hold in reevaluation.Parked)
        {
            logger.LogWarning(
                "Task {TaskId} is held for a human rather than unblocked: {Reason}",
                hold.TaskId, hold.Reason);
        }

        // A lifted hold is only good news when nothing dead is left behind it. A dependent
        // waiting on two dead blockers, one of them retried, is still NeedsHuman on h9k status,
        // so announcing "waits normally again" would say the opposite of what the human reads
        // on the task itself (review finding, 2026-08-21).
        foreach (DependencyRecovery recovery in reevaluation.Recovered)
        {
            if (recovery.SurvivingReason is null)
            {
                logger.LogInformation(
                    "Task {TaskId}'s dead blocker is back in the pipeline — the hold is lifted "
                    + "and it waits normally again",
                    recovery.TaskId);
                continue;
            }

            logger.LogWarning(
                "Task {TaskId}'s dead blocker is back in the pipeline, but it is still held for "
                + "a human by another one: {Reason}",
                recovery.TaskId, recovery.SurvivingReason);
        }

        if (reevaluation.Unblocked.Count > 0)
        {
            logger.LogInformation(
                "{Count} blocked task(s) had their last dependency close out — moved Blocked → Queued",
                reevaluation.Unblocked.Count);
            await Doorbell.RingAsync(connection.ConnectionString, "dependencies-met", cancellationToken);
        }

        return reevaluation;
    }

    /// <summary>
    /// Claim this owner's queued tasks while the node is under its concurrency ceiling. The
    /// claim is the lock: appends race on the stream version and the database picks the winner
    /// (TASK-MODEL.md §2). Draft, Published and Blocked tasks are structurally invisible here —
    /// a task becomes claimable only through an explicit human assignment (Decisions Log #34).
    /// <para>
    /// Everything the ceiling turns away simply stays Queued, which already honestly means
    /// waiting, and is claimed as slots free up in the same order the queue always had. There
    /// is no throttled state and no reservation: nothing is written about a deferral beyond the
    /// load measurement <see cref="NodeDispatchLoad"/> carries for the attention pane.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ClaimedWork>> ClaimEligibleAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        NodeLoad load = await MeasureLoadAsync(session, cancellationToken);

        // The whole claim rule, as one indexed-friendly filter (Decisions Log #34): Queued
        // means a human assigned it and every dependency has closed out, and the owner match
        // means those were this node's owner's decisions. The ceiling shapes how much of this
        // set is taken, never which end of it (Decisions Log #64).
        //
        // Oldest assignment first, because assignment is the act that put the task in this
        // queue: a task drafted in January and assigned this morning is behind one drafted and
        // assigned last week, which ordering by AddedAt alone would get backwards. AddedAt
        // still breaks ties, which is what the bulk import of a backlog assigned in one command
        // looks like. Every row here has an assignedAt — the projection writes one for
        // pre-lifecycle streams too, and the startup backfill rebuilds any document old enough
        // to be missing the key before this query runs.
        Guid ownerId = node.OwnerId;
        IReadOnlyList<TaskListItem> queued = await session.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Queued.Value))
            .Where(t => t.AssignedOwnerId == ownerId)
            .OrderBy(t => t.AssignedAt)
            .ThenBy(t => t.AddedAt)
            .ToListAsync(cancellationToken);

        // The whole queue is read rather than just the claimable head, so every task the
        // ceiling defers can be named in the log exactly once. It is the queue of one owner's
        // ready work, which is the same set the attention pane already loads in full.
        List<ClaimedWork> claimed = [];
        List<Guid> deferred = [];
        foreach (TaskListItem candidate in queued)
        {
            if (claimed.Count >= load.Capacity)
            {
                deferred.Add(candidate.Id);
                continue;
            }

            if (await TryClaimAsync(candidate.Id, cancellationToken) is { } work)
            {
                claimed.Add(work);
            }
        }

        await PublishLoadAsync(session, load, claimed.Count, deferred, cancellationToken);
        return claimed;
    }

    /// <summary>
    /// Publish what the node is carrying and name what the ceiling turned away — after the
    /// claims, and never at their expense.
    /// <para>
    /// Both the record and the log describe the node as the sweep leaves it, not as it found it:
    /// the sweep that fills the node is precisely the one whose deferrals need explaining, and
    /// publishing the count it started with would have it report spare capacity while it turns
    /// work away — for as long as the launches it just handed back take, which is minutes on a
    /// fan-in dispatch. Re-measured rather than added up, so the published number stays something
    /// this node observed (AGENTS.md: never guess at unobserved facts); the measurement is
    /// skipped only when this sweep changed nothing.
    /// </para>
    /// <para>
    /// All of it is telemetry, so all of it is caught. Every task claimed above is already leased
    /// in its own committed transaction, and letting a failed read or a failed document write
    /// throw out of <see cref="ClaimEligibleAsync"/> would drop that list on the floor before
    /// <c>RunLauncher</c> ever saw it — and a lease with no run behind it is stranded for good:
    /// the heartbeat service keeps refreshing it, so the expiry sweep never finds it, and with no
    /// run document written, startup adoption cannot find it either. Those leases would then
    /// count as live forever and wedge the whole queue. A missing load measurement costs one
    /// stale number on the attention pane until the next sweep. Origin incident (2026-08-22):
    /// pre-PR review of this branch.
    /// </para>
    /// </summary>
    private async Task PublishLoadAsync(
        IDocumentSession session,
        NodeLoad measured,
        int claimedCount,
        IReadOnlyCollection<Guid> deferred,
        CancellationToken cancellationToken)
    {
        try
        {
            NodeLoad carried = claimedCount == 0
                ? measured
                : await MeasureLoadAsync(session, cancellationToken);
            await RecordLoadAsync(session, carried, DateTimeOffset.UtcNow, cancellationToken);
            ReportDeferrals(deferred, carried);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The deferred set is left as it was, so the next sweep that gets this far announces
            // the tasks this one could not.
            logger.LogWarning(
                exception,
                "Recording this node's dispatch load failed; {Claimed} claim(s) are unaffected and h9k status "
                + "will read the previous measurement until the next sweep",
                claimedCount);
        }
    }

    /// <summary>
    /// The live-run count this node claims against, and the session ceiling it is measured
    /// against (Decisions Log #64). The counting rule itself is <see cref="NodeLoad.LiveSlots"/>;
    /// the conversion from run trees to resident sessions is <see cref="NodeLoad"/>'s; the
    /// two queries here are just what it needs: this node's leases, and the runs that could
    /// answer for them.
    /// </summary>
    private async Task<NodeLoad> MeasureLoadAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        IReadOnlyList<TaskLease> leases = await session.Query<TaskLease>()
            .Where(lease => lease.NodeId == nodeId)
            .ToListAsync(cancellationToken);

        IReadOnlyList<RunListItem> live = await session.Query<RunListItem>()
            .Where(run => run.NodeId == nodeId)
            .Where(run => run.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value,
                RunState.Verifying.Value, RunState.UnderReview.Value))
            .ToListAsync(cancellationToken);

        // The leased tasks' runs whatever state they are in, because "no run at this
        // generation yet" and "a run that has already parked" are the two answers the rule
        // tells apart, and neither shows up in the query above.
        Guid[] leased = [.. leases.Select(lease => lease.Id)];
        IReadOnlyList<RunListItem> leaseRuns = leased.Length == 0
            ? []
            : await session.Query<RunListItem>()
                .Where(run => run.NodeId == nodeId && run.TaskId.IsOneOf(leased))
                .ToListAsync(cancellationToken);

        List<RunListItem> runs = [.. live, .. leaseRuns.Where(run => live.All(other => other.Id != run.Id))];
        return new NodeLoad(NodeLoad.LiveSlots(nodeId, leases, runs).Count, _options.MaxConcurrentAgentSessions);
    }

    /// <summary>
    /// Publish what the node is carrying as the sweep leaves it, so h9k status can say a quiet
    /// board is throttled rather than stalled without re-deriving the count (Decisions Log #64).
    /// Written on every sweep, including the ones with spare capacity: a reader needs to know the
    /// measurement is current as much as it needs the number.
    /// </summary>
    private async Task RecordLoadAsync(
        IDocumentSession session, NodeLoad load, DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Store(new NodeDispatchLoad
        {
            Id = node.NodeId,
            MachineName = Environment.MachineName,
            LiveRuns = load.LiveRuns,
            MaxConcurrentRuns = load.MaxConcurrentRuns,
            LiveAgentSessions = load.LiveAgentSessions,
            MaxConcurrentAgentSessions = load.MaxConcurrentAgentSessions,
            ObservedAt = now,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One line per deferral, not one per sweep (Decisions Log #64). The set is replaced rather
    /// than added to, so a task announced once goes quiet while it waits and is announced again
    /// only if it leaves the deferred set and comes back.
    /// </summary>
    private void ReportDeferrals(IReadOnlyCollection<Guid> deferred, NodeLoad load)
    {
        foreach (Guid taskId in deferred.Where(taskId => !_deferredClaims.Contains(taskId)))
        {
            logger.LogInformation(
                "Task {TaskId} stays queued: this node is at its concurrency ceiling "
                + "({LiveRuns} of {MaxConcurrentRuns} live run(s), reserving {LiveAgentSessions} of "
                + "{MaxConcurrentAgentSessions} agent session(s)) — it is claimed as a slot frees up",
                taskId, load.LiveRuns, load.MaxConcurrentRuns,
                load.LiveAgentSessions, load.MaxConcurrentAgentSessions);
        }

        _deferredClaims.Clear();
        _deferredClaims.UnionWith(deferred);
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
