using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
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
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
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
    LaunchHoldEngine launchHold,
    IOptions<DaemonOptions> options,
    ILogger<DispatchEngine> logger,
    TrackerClaimGate? trackerClaimGate = null)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// The project claim gate this node's claims go through (idea 64c75e43). Optional so a test
    /// that never touches a gated project constructs this engine as it always has; the default is
    /// the real gate, which for an ungated project makes not one call.
    /// </summary>
    private readonly TrackerClaimGate _trackerClaimGate = trackerClaimGate ?? new TrackerClaimGate();

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
    /// Tasks already reported as deferred by their own project's cap (Decisions Log #140) — a
    /// separate set from <see cref="_deferredClaims"/>, because the two are independent limits
    /// and a task turned away by one must not be silently folded into the other's log line. Same
    /// rebuilt-every-sweep discipline: a task that gets claimed and later queues behind the cap
    /// again is announced again.
    /// </summary>
    private readonly HashSet<Guid> _deferredByProjectCap = [];

    /// <summary>
    /// Whether the last measurement found this node over its ceiling, so the overshoot is stated
    /// once per episode rather than every five seconds for as long as a review cycle runs.
    /// Cleared the moment the node is back under, which is what makes the next one a fresh line.
    /// </summary>
    private bool _reportedOverCeiling;

    /// <summary>
    /// Tasks already reported as deferred by the spend budget, the same one-line-per-episode
    /// discipline <see cref="_deferredClaims"/> gives the concurrency ceiling — a distinct set,
    /// because the two gates are independent causes and a task turned away by one must not be
    /// silently folded into the other's log line.
    /// </summary>
    private readonly HashSet<Guid> _deferredBySpend = [];

    /// <summary>Mirrors <see cref="_reportedOverCeiling"/> for the spend budget: stated once per episode, not once per sweep.</summary>
    private bool _reportedSpendExhausted;

    /// <summary>
    /// When this node last dispatched for each project — the rotation's whole memory (Decisions
    /// Log #141), written only by a claim that actually committed. A project absent from it is
    /// unserved and so outranks every project that has been served, which is what makes the
    /// rotation starvation-proof without a counter to reconcile.
    /// <para>
    /// In memory, per daemon process, exactly like <see cref="_deferredClaims"/>,
    /// <see cref="_reportedOverCeiling"/> and <see cref="_spendExhaustedSincePeriodStart"/>: every
    /// other thing this engine remembers between sweeps lives here too, and none of it is worth a
    /// durable record of its own. A cold start therefore reads every project as unserved, and the
    /// documented tie-break takes over — the queue's own order, so the first claim after a restart
    /// is exactly the claim the platform would have made before any of this existed. A restart is
    /// not a fairness hole: the rotation re-forms from the first slot onward, and one uneven
    /// first slot cannot starve anything, because being served is what makes a project yield.
    /// </para>
    /// <para>
    /// An interactive claim (<c>h9k task work</c>, <c>h9k task start</c>) never lands here: it
    /// costs no slot at either ceiling (Decisions Log #111, #140) and this dispatcher never made
    /// it, so counting it as this node having served that project would make a rotation turn out
    /// of something that consumed nothing.
    /// </para>
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _lastServedByProject = [];
    /// When this node last actually read the tracker for a task its claim gate turned away (idea
    /// 64c75e43), keyed by task. It is what keeps the gate off the tracker's back: the dispatch
    /// loop sweeps roughly every <see cref="DaemonOptions.PollInterval"/> — five seconds — and a
    /// card assigned to a teammate would otherwise cost one Jira or GitHub call every five
    /// seconds, indefinitely, for as long as it sits in the queue. A held task is re-read no more
    /// often than <see cref="DaemonOptions.PullRequestPollInterval"/> instead, the same
    /// human-timescale cadence closeout polls pull requests at and for the same reason (Decisions
    /// Log #22): an assignment is a human act, and three minutes of latency on picking it up is
    /// invisible next to the round trip of a person noticing a card.
    /// <para>
    /// Only a refusal records a timestamp, and a pass clears it: a check that passed and then lost
    /// the claim race must be re-read on the very next sweep rather than treated as still held for
    /// three minutes on the strength of a read that said the opposite.
    /// </para>
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _trackerGateReadAt = [];

    /// <summary>
    /// What this node last said in the log about each tracker-held task, so the hold is stated
    /// once per episode rather than once per sweep — the same discipline
    /// <see cref="ReportDeferrals"/> gives the concurrency ceiling. The reason itself is the key,
    /// not merely the task id: a card that moves from one teammate to another, or from held to
    /// unreadable, is a different hold and worth a fresh line, while the same hold repeating for
    /// an hour is not.
    /// </summary>
    private readonly Dictionary<Guid, string> _reportedTrackerHolds = [];

    /// <summary>
    /// The period start <see cref="SpendBudgetExhaustedAsync"/> last confirmed exhausted, or null
    /// when the current period isn't known to be. Spend within a period only ever grows —
    /// <c>TokensRecorded</c> is append-only — so once a period is exhausted it stays exhausted
    /// until the period rolls over, and re-summing every event on every sweep to confirm the same
    /// answer again is pure cost: a node that spends its weekly budget on day one with tasks still
    /// queued would otherwise re-materialize the whole period's events roughly every
    /// <see cref="DaemonOptions.PollInterval"/> for the remainder of the week (independent pre-PR
    /// review, cycle 7, adversarial lens). A periodStart that doesn't match invalidates the cache
    /// on its own, so a rollover re-queries exactly once, starting the new period unexhausted.
    /// </summary>
    private DateTimeOffset? _spendExhaustedSincePeriodStart;

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

            if (run is not null
                && (run.State == RunState.ReviewParked || run.State == RunState.CloseoutParked
                    || run.State == RunState.BudgetParked || run.State == RunState.LaunchHeld))
            {
                // CloseoutParked normally holds a Done, lease-free task, but it is included so
                // the guarantee is a property of "parked", not of one park flavor. BudgetParked
                // is parked on the clock rather than a human, but the same guarantee applies —
                // a lease that goes stale while the daemon catches up must not requeue (and so
                // fail) work that is waiting to be retried automatically (backlog 40). LaunchHeld
                // needs it even more (task: a session that exits at once with no work done is
                // treated as the node failing to launch sessions): a daemon restart mid-outage is
                // exactly the gap this exclusion exists for, and the acceptance criterion is that
                // a restart resumes the hold rather than requeuing the work it is holding.
                lease.HeartbeatAt = now;
                session.Store(lease);
                logger.LogInformation(
                    "Lease on task {TaskId} expired but its run is parked — lease refreshed, not requeued",
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
                && !await RunResultFile.AlreadyWrittenAsync(
                    RunPaths.ResolveCurrentDirectory(run.RunDirectory), cancellationToken))
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
    /// Claim this owner's queued tasks while the node is under its concurrency ceiling and each
    /// task's own project is under its (Decisions Log #64, #111, #140). The claim is the lock:
    /// appends race on the stream version and the database picks the winner (TASK-MODEL.md §2).
    /// Draft, Published and Blocked tasks are structurally invisible here — a task becomes
    /// claimable only through an explicit human assignment (Decisions Log #34).
    /// <para>
    /// Which project each free slot goes to is <see cref="ProjectRotation"/>'s decision
    /// (Decisions Log #141): round-robin across the eligible projects by default, with an optional
    /// priority tier over it, asked once per slot because this sweep's own claims change who is
    /// eligible as it goes. Every claim logs the one sentence naming the winner and why.
    /// </para>
    /// <para>
    /// Everything either ceiling turns away simply stays Queued, which already honestly means
    /// waiting, and is claimed as slots free up. There is no throttled state and no reservation:
    /// nothing is written about a deferral beyond the load measurement
    /// <see cref="NodeDispatchLoad"/> carries for the attention pane. A project cap is a ceiling
    /// in exactly that sense — nothing is set aside for an idle project, so a project capped above
    /// its share simply fills whatever the node and the other projects' activity leave free. The
    /// rotation adds no third cause: a project that loses a slot is turned away by the node
    /// ceiling or by its own cap, and that is what its deferral names.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ClaimedWork>> ClaimEligibleAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Read once per sweep and threaded through both the queue read and the claim itself
        // (task: a project can be archived, listed as archived, reactivated, and renamed): an
        // archived project's tasks are never even candidates here, which is the primary way "the
        // dispatcher never claims its tasks" holds, and TryClaimAsync's own check below is a
        // belt-and-suspenders second look — the same discipline this method already gives a task's
        // own state (re-validated in TryClaimAsync right before the claim commits).
        IReadOnlySet<Guid> archivedProjects = await ReadArchivedProjectIdsAsync(session, cancellationToken);
        IReadOnlyList<QueuedCandidate> queued = await ReadQueueAsync(session, archivedProjects, cancellationToken);

        // Measured after the queue is read rather than before it, because a project cap can only
        // be measured against the projects that actually have a candidate this sweep — a paused
        // project with queued work has to publish its own row even though it is carrying nothing.
        Guid[] queuedProjects = [.. queued.Select(candidate => candidate.ProjectId).Distinct()];
        DispatchLoad load = await MeasureLoadAsync(session, queuedProjects, cancellationToken);

        // Worth the scan only when something could actually be claimed: an idle sweep with
        // nothing queued, a node already at its concurrency ceiling, or a sweep where every
        // queued candidate's own project is full or paused, would otherwise materialize a full
        // period's worth of TokensRecorded events for a decision that changes nothing about this
        // sweep's outcome — paid every PollInterval regardless (independent pre-PR review,
        // cycle 1, adversarial lens; the project clause, PR review round 1, for the paused
        // project on an otherwise idle node). Skipping the scan costs nothing in honesty: the
        // rows are then reported against the cap that actually holds them, which is the truer
        // cause anyway, since a bigger spend budget would not release a task its own project is
        // holding back. This guard alone does not bound the steady state a spent budget
        // produces — all three conditions stay true for as long as the gate holds tasks Queued
        // rather than claiming them — so SpendBudgetExhaustedAsync's own cache (independent
        // pre-PR review, cycle 7, adversarial lens) is what actually keeps that case cheap.
        bool anyProjectAdmits = queued.Any(
            candidate => load.Project(candidate.ProjectId).Ceiling.Admits(claimedThisSweep: 0));
        bool spendExhausted = queued.Count > 0 && load.Node.Capacity > 0 && anyProjectAdmits
            && await SpendBudgetExhaustedAsync(session, cancellationToken);

        // A node-wide launch hold (task: a session that exits at once with no work done is
        // treated as the node failing to launch sessions): this node could not launch a working
        // session at all, so claiming anything new would only strand another task the same way.
        // Same gating shape as the spend budget immediately above — a cheap check skipped
        // entirely when nothing queued could be claimed anyway — but a single indexed doc read
        // (LaunchHoldEngine.CurrentHoldAsync) rather than a full-period event resummation, so it
        // needs no in-memory cache of its own the way SpendBudgetExhaustedAsync's does.
        bool launchHoldGateNeeded = queued.Count > 0 && load.Node.Capacity > 0 && anyProjectAdmits;

        List<ClaimedWork> claimed = [];
        Dictionary<Guid, int> claimedByProject = [];
        List<QueuedCandidate> waiting = [.. queued];

        // One slot at a time, each with its own winner and its own recorded reason (Decisions Log
        // #141). The rotation is asked again per slot rather than once per sweep because this
        // sweep's own claims move the answer: a project that just took a slot is no longer the
        // longest unserved, and one that just reached its cap is no longer eligible at all. The
        // spend gate is checked once, in the loop's own condition, since nothing this loop does
        // spends tokens itself. The launch hold is re-read on every slot instead (Copilot review,
        // PR #317): a single read taken before this loop started can go stale mid-sweep — another
        // run's own launch failure raising the hold while this loop still has several slots left
        // to fill — and claiming those slots into an outage the very next read would have caught
        // is exactly the strand this gate exists to prevent.
        while (!spendExhausted
            && claimed.Count < load.Node.Capacity
            && ProjectRotation.NextSlot(waiting, load, claimedByProject, _lastServedByProject) is { } slot)
        {
            if (launchHoldGateNeeded
                && await launchHold.CurrentHoldAsync(node.NodeId, cancellationToken) is { LaunchHoldActive: true })
            {
                break;
            }

            // Removed whether or not the claim lands: a candidate that lost the claim race (or
            // whose previous generation is still alive here) is not this sweep's to place, and
            // leaving it in would spin the loop on the same task until the capacity ran out.
            waiting.Remove(slot.Candidate);

            if (await TryClaimAsync(slot.Candidate.TaskId, cancellationToken) is { } work)
            {
                claimed.Add(work);
                claimedByProject[slot.Candidate.ProjectId] =
                    claimedByProject.GetValueOrDefault(slot.Candidate.ProjectId) + 1;

                // Served is recorded only for a claim that actually committed, so a lost race
                // costs a project nothing in the rotation — it was never dispatched for.
                _lastServedByProject[slot.Candidate.ProjectId] = DateTimeOffset.UtcNow;
                ReportSlotClaimed(slot, load);
            }
        }

        // Whatever is still waiting is deferred, and named against the limit that actually holds
        // it. The project's own cap is asked before either of this node's limits, so a task its
        // project is holding back is never reported against a node-level lever that would not
        // release it: neither a raised ceiling nor a rolled-over period starts a paused project's
        // work, and the cap is the lever that owns those rows. Both surfaces resolve the limits in
        // this same order (Hall9k.Cli.Commands.QueueHold), so the daemon log and h9k status can
        // never name different causes for one row.
        //
        // The cap is asked ahead of the spend gate specifically for the mixed sweep the
        // anyProjectAdmits guard above cannot cover: one project paused (or full) while another
        // admits, on a spent budget. That guard reads such a sweep as worth scanning — correctly,
        // since the admitting project's rows really are held by the budget — and gating on spend
        // first would then sweep the paused project's rows into the same log line, promising they
        // are "claimed once the period rolls" when the rollover releases nothing, while h9k status
        // went on naming the pause for the very same row (independent pre-PR review, cycle 1,
        // adversarial lens).
        List<QueuedCandidate> deferredByProjectCap = [.. waiting.Where(candidate =>
            !load.Project(candidate.ProjectId).Ceiling
                .Admits(claimedByProject.GetValueOrDefault(candidate.ProjectId)))];
        Guid[] deferredByNode = [.. waiting.Except(deferredByProjectCap).Select(candidate => candidate.TaskId)];
        Guid[] deferredBySpend = spendExhausted ? deferredByNode : [];
        Guid[] deferredByCeiling = spendExhausted ? [] : deferredByNode;

        await PublishLoadAsync(
            session, load, queuedProjects, claimed.Count, deferredByCeiling, deferredByProjectCap, cancellationToken);
        ReportSpendExhausted(spendExhausted, deferredBySpend);
        ForgetTrackerHoldsOutsideTheQueue(queued);
        return claimed;
    }

    /// <summary>
    /// One plain sentence per claim, naming the project that won the free slot and why (Decisions
    /// Log #141's hard requirement): the reason rides the claim rather than being reconstructible
    /// only from a scheduler state dump, so any dispatch decision is explainable from the log
    /// alone, the same discipline the deferral lines already follow from the other side.
    /// <para>
    /// One call per reason rather than one template with substituted clauses, because the numbers
    /// each reason states differ, and a shared template would either drop them or print a
    /// placeholder against the wrong argument (the lesson
    /// <see cref="ReportProjectCapDeferrals"/>'s own two templates carry).
    /// </para>
    /// </summary>
    private void ReportSlotClaimed(RotationSlot slot, DispatchLoad load)
    {
        string project = load.Project(slot.Candidate.ProjectId).Name;
        switch (slot.Reason)
        {
            case SlotReason.QueueFirstMarker:
                logger.LogInformation(
                    "Free slot to project {Project} (task {TaskId}): a human marked this task queue-first, "
                    + "which takes the next free slot ahead of the rotation and of every tier, and clears "
                    + "itself as this claim commits",
                    project, slot.Candidate.TaskId);
                break;
            case SlotReason.PriorityTier:
                logger.LogInformation(
                    "Free slot to project {Project} (task {TaskId}): priority {Priority} outranks the rotation "
                    + "while this project has ready work, of {EligibleProjects} project(s) with any — it "
                    + "releases itself the moment its queue drains",
                    project, slot.Candidate.TaskId, slot.Priority.Value.ToLowerInvariant(), slot.EligibleProjects);
                break;
            case SlotReason.LongestUnserved when slot.LastServedAt is { } servedAt:
                logger.LogInformation(
                    "Free slot to project {Project} (task {TaskId}): longest unserved of {EligibleProjects} "
                    + "project(s) with ready work — last dispatched for at {LastServedAt:u}, oldest task first "
                    + "within it",
                    project, slot.Candidate.TaskId, slot.EligibleProjects, servedAt);
                break;
            case SlotReason.LongestUnserved:
                logger.LogInformation(
                    "Free slot to project {Project} (task {TaskId}): longest unserved of {EligibleProjects} "
                    + "project(s) with ready work — nothing has been dispatched for it since this daemon "
                    + "started, oldest task first within it",
                    project, slot.Candidate.TaskId, slot.EligibleProjects);
                break;
            case SlotReason.OnlyEligibleProject:
            default:
                logger.LogInformation(
                    "Free slot to project {Project} (task {TaskId}): the only project with ready work under "
                    + "every applicable limit this sweep — oldest task first within it",
                    project, slot.Candidate.TaskId);
                break;
        }
    }

    /// <summary>
    /// This owner's queue, in the order the queue itself is served.
    /// <para>
    /// The whole claim rule, as one indexed-friendly filter (Decisions Log #34): Queued
    /// means a human assigned it and every dependency has closed out, and the owner match
    /// means those were this node's owner's decisions. The ceilings shape how much of this
    /// set is taken, never which end of it (Decisions Log #64).
    /// </para>
    /// <para>
    /// This order is the queue's own, and it stays what decides <em>within</em> one project and
    /// what breaks a tie between two equally unserved ones (Decisions Log #141). Across projects
    /// under contention it is <see cref="ProjectRotation"/> that picks, so this list is the set
    /// and the tie-break rather than the running order.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<QueuedCandidate>> ReadQueueAsync(
        IQuerySession session, IReadOnlySet<Guid> archivedProjects, CancellationToken cancellationToken)
    {
        Guid ownerId = node.OwnerId;

        // Marker first (task 45136b29, idea fcaded0b's R7 ruling): a task a human recorded as
        // queue-first (h9k task revise --queue-first, or h9k task handback --first) takes the
        // next free slot regardless of assignment age, ahead of every unmarked row — the marker
        // clears the moment this same claim commits (TaskListItem.Apply(TaskClaimed)), so it
        // never survives past the turn it bought. Everything unmarked falls through to the
        // ordering that already existed and is unchanged: oldest assignment first, because
        // assignment is the act that put the task in this queue: a task drafted in January and
        // assigned this morning is behind one drafted and assigned last week, which ordering by
        // AddedAt alone would get backwards. AddedAt still breaks ties, which is what the bulk
        // import of a backlog assigned in one command looks like. Every row here has an
        // assignedAt — the projection writes one for pre-lifecycle streams too, and the startup
        // backfill rebuilds any document old enough to be missing the key before this query runs.
        // The same is true of queuePriorityMarked itself: a document old enough to predate the
        // marker is missing that key too, and OrderByDescending over a missing key sorts it NULL
        // first under Postgres's default DESC ordering, ahead of a genuinely marked row, so the
        // same startup backfill rebuilds it before this query runs. What that ordering can still
        // get wrong is narrower than it was: the marker's own promise no longer rides on it, since
        // ProjectRotation picks the marked candidate by value (Decisions Log #141), leaving the
        // NULL to misplace a stale row only among the unmarked ones.
        //
        // Three fields, never the documents: nothing below reads any other projection field.
        // TryClaimAsync decides from the task's own stream, a deferral is logged by id and by the
        // project whose cap held it, and the rotation needs the project and the marker, so every
        // other document body fetched here would be deserialized and dropped. It is worth saying
        // because of what follows — the whole queue
        // is read rather than just the claimable head, so that every task either ceiling defers
        // can be named in the log exactly once, which makes this the one read here whose size
        // grows with the backlog rather than with the ceiling.
        IReadOnlyList<QueuedRow> rows = await session.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Queued.Value))
            .Where(t => t.AssignedOwnerId == ownerId)
            .OrderByDescending(t => t.QueuePriorityMarked)
            .ThenBy(t => t.AssignedAt)
            .ThenBy(t => t.AddedAt)
            .Select(t => new QueuedRow(t.Id, t.ProjectId, t.QueuePriorityMarked))
            .ToListAsync(cancellationToken);

        // An archived project's tasks stay Queued in the database (h9k project remove already
        // refused to archive over a Queued one, so this can only be reached if a project was
        // archived and the task predates that — never through the ordinary path), but they are
        // filtered out here rather than merely deferred: "the dispatcher never claims its tasks"
        // means invisible, not held with a reason the way a paused project's own cap holds one.
        return [.. rows
            .Where(row => !archivedProjects.Contains(row.ProjectId))
            .Select(row => new QueuedCandidate(row.Id, row.ProjectId, row.QueuePriorityMarked ?? false))];
    }

    /// <summary>
    /// Every project archived on this install, read once per sweep (task: a project can be
    /// archived, listed as archived, reactivated, and renamed) — a plain bool column, unlike the
    /// value-object-backed fields elsewhere in this file that need <c>MatchesSql</c> to filter on
    /// server-side, so a direct LINQ <c>Where</c> translates cleanly.
    /// </summary>
    private static async Task<IReadOnlySet<Guid>> ReadArchivedProjectIdsAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> ids = await session.Query<ProjectDetails>()
            .Where(project => project.IsArchived)
            .Select(project => project.Id)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    /// <summary>
    /// The three selected columns, with the marker <em>nullable</em> — the shape a document
    /// written before the marker existed actually has (see the ordering note above: it carries no
    /// such key at all). Selected into a non-nullable bool, a missing key deserializes as JSON
    /// null against a bool and throws, which would take the whole sweep down and wedge the queue
    /// rather than merely misordering one row — a far worse failure than the one the startup
    /// backfill exists to repair, and reachable by any sweep that races that repair. Absent reads
    /// as unmarked, which is what an absent marker means. Origin incident (2026-09-06): the queue
    /// read's own first version selected it as a bool and
    /// <c>TaskProjectionBackfillTests.A_stale_unmarked_document_does_not_outrank_a_marked_one_before_or_after_the_backfill</c>
    /// — which strips exactly that key — failed on the deserialization.
    /// </summary>
    private sealed record QueuedRow(Guid Id, Guid ProjectId, bool? QueuePriorityMarked);

    /// <summary>
    /// Whether this node's periodic token-spend budget (backlog: spend-governor step three) is
    /// spent for the period containing now. Summed live from every <c>TokensRecorded</c> event
    /// since the period start rather than a stored counter — a daemon restart can neither lose
    /// nor double-count it, because there is nothing to lose. Null <see cref="DaemonOptions.SpendBudgetTokens"/>
    /// means no budget, the unchanged, unbudgeted default. <see cref="_spendExhaustedSincePeriodStart"/>
    /// short-circuits every sweep after the first that confirms this period exhausted, since spend
    /// only grows within a period and the answer cannot un-confirm itself before the next rollover.
    /// </summary>
    private async Task<bool> SpendBudgetExhaustedAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        if (_options.SpendBudgetTokens is not { } budget)
        {
            return false;
        }

        SpendPeriod period = SpendPeriod.FromInput(_options.SpendPeriod);
        DateTimeOffset periodStart = period.StartOf(DateTimeOffset.UtcNow);

        if (_spendExhaustedSincePeriodStart == periodStart)
        {
            return true;
        }

        PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
        bool exhausted = spend.TotalInputTokens >= budget;
        _spendExhaustedSincePeriodStart = exhausted ? periodStart : null;
        return exhausted;
    }

    /// <summary>
    /// One line per episode, the spend budget's own mirror of <see cref="ReportOverCeiling"/> and
    /// <see cref="ReportDeferrals"/>: a distinct cause from the concurrency ceiling, so it earns
    /// its own log line naming the actual mechanism rather than folding into the ceiling's.
    /// </summary>
    private void ReportSpendExhausted(bool exhausted, IReadOnlyCollection<Guid> deferred)
    {
        if (!exhausted)
        {
            _reportedSpendExhausted = false;
            _deferredBySpend.Clear();
            return;
        }

        if (!_reportedSpendExhausted)
        {
            _reportedSpendExhausted = true;
            logger.LogWarning(
                "This node's spend budget for the current period is spent — nothing further is claimed until "
                + "the period rolls ({Count} queued task(s) affected this sweep)",
                deferred.Count);
        }

        foreach (Guid taskId in deferred.Where(taskId => !_deferredBySpend.Contains(taskId)))
        {
            logger.LogInformation(
                "Task {TaskId} stays queued: this node's spend budget for the current period is spent — it is "
                + "claimed once the period rolls",
                taskId);
        }

        _deferredBySpend.Clear();
        _deferredBySpend.UnionWith(deferred);
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
        DispatchLoad measured,
        IReadOnlyCollection<Guid> queuedProjects,
        int claimedCount,
        IReadOnlyCollection<Guid> deferred,
        IReadOnlyCollection<QueuedCandidate> deferredByProjectCap,
        CancellationToken cancellationToken)
    {
        try
        {
            DispatchLoad carried = claimedCount == 0
                ? measured
                : await MeasureLoadAsync(session, queuedProjects, cancellationToken);
            await RecordLoadAsync(session, carried, DateTimeOffset.UtcNow, cancellationToken);
            ReportOverCeiling(carried.Node);
            ReportDeferrals(deferred, carried.Node);
            ReportProjectCapDeferrals(deferredByProjectCap, carried);
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
    /// The live-run count this node claims against, and the run ceiling it is measured against
    /// (Decisions Log #64, #111), plus the same count and cap per interested project (#140). The
    /// counting rule itself is <see cref="NodeLoad.LiveSlots"/>; the queries here are just what it
    /// needs: this node's leases, the runs that could answer for them, and — for the per-project
    /// split — which project each of those live slots belongs to.
    /// </summary>
    /// <param name="queuedProjects">
    /// The projects this sweep has candidates from, so a project carrying nothing still gets a
    /// measured row: a paused project's whole point is that it is holding work while idle, and a
    /// row absent from the published measurement would leave <c>h9k status</c> with nothing to
    /// say about it.
    /// </param>
    private async Task<DispatchLoad> MeasureLoadAsync(
        IQuerySession session, IReadOnlyCollection<Guid> queuedProjects, CancellationToken cancellationToken)
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
        IReadOnlyCollection<LiveSlot> slots = NodeLoad.LiveSlots(nodeId, leases, runs);

        return new DispatchLoad(
            new NodeLoad(slots.Count, _options.MaxConcurrentTaskRuns),
            await MeasureProjectLoadsAsync(session, slots, queuedProjects, cancellationToken));
    }

    /// <summary>
    /// The same live slots, split by the project each one belongs to, against each project's own
    /// cap (Decisions Log #140). The split is read off the slots' own task documents rather than
    /// counted from a second query, so the per-project numbers and the node number can never
    /// disagree about which slots are live — one measurement, two denominators.
    /// <para>
    /// A slot whose task document cannot be read counts toward the node's number (the machine is
    /// holding it either way) and toward no project's, because which project it belongs to is
    /// then genuinely unobserved. The load stays bounded by the ceiling rather than by the
    /// backlog: only live slots' tasks are fetched, never the queue's.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, ProjectLoad>> MeasureProjectLoadsAsync(
        IQuerySession session,
        IReadOnlyCollection<LiveSlot> slots,
        IReadOnlyCollection<Guid> queuedProjects,
        CancellationToken cancellationToken)
    {
        Guid[] slotTasks = [.. slots.Select(slot => slot.TaskId).Distinct()];
        IReadOnlyList<TaskListItem> slotTaskRows = slotTasks.Length == 0
            ? []
            : await session.Query<TaskListItem>()
                .Where(task => task.Id.IsOneOf(slotTasks))
                .ToListAsync(cancellationToken);

        Dictionary<Guid, Guid> projectByTask = slotTaskRows.ToDictionary(task => task.Id, task => task.ProjectId);
        Dictionary<Guid, int> liveByProject = [];
        foreach (LiveSlot slot in slots)
        {
            if (projectByTask.TryGetValue(slot.TaskId, out Guid projectId))
            {
                liveByProject[projectId] = liveByProject.GetValueOrDefault(projectId) + 1;
            }
        }

        // Every project this sweep could have to decide about: one it is carrying a run for, or
        // one a queued candidate belongs to.
        Guid[] measured = [.. new HashSet<Guid>([.. liveByProject.Keys, .. queuedProjects])];
        if (measured.Length == 0)
        {
            return new Dictionary<Guid, ProjectLoad>();
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.Id.IsOneOf(measured))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, ProjectDetails> byId = projects.ToDictionary(project => project.Id);

        return measured.ToDictionary(
            projectId => projectId,
            projectId =>
            {
                ProjectDetails? details = byId.GetValueOrDefault(projectId);
                return new ProjectLoad(
                    projectId,
                    details?.Name ?? projectId.ToString(),
                    new ProjectRunCeiling(liveByProject.GetValueOrDefault(projectId), details?.MaxParallelTasks),
                    // The tier comes off the same document read as the cap (Decisions Log #141),
                    // so a sweep can never rotate on one project's tier and admit against another
                    // moment's cap. A project no document answered for rotates in the default
                    // tier, the same honest fallback its cap takes.
                    details?.Priority ?? ProjectPriority.Normal);
            });
    }

    /// <summary>
    /// Publish what the node is carrying as the sweep leaves it, so h9k status can say a quiet
    /// board is throttled rather than stalled without re-deriving the count (Decisions Log #64).
    /// Written on every sweep, including the ones with spare capacity: a reader needs to know the
    /// measurement is current as much as it needs the number.
    /// </summary>
    private async Task RecordLoadAsync(
        IDocumentSession session, DispatchLoad load, DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Store(new NodeDispatchLoad
        {
            Id = node.NodeId,
            MachineName = Environment.MachineName,
            LiveRuns = load.Node.LiveRuns,
            MaxConcurrentRuns = load.Node.MaxConcurrentRuns,
            ObservedAt = now,
            SpendBudgetTokens = _options.SpendBudgetTokens,
            SpendPeriod = _options.SpendPeriod,
            // Published rather than left to the reader to re-derive, the same reason
            // MaxConcurrentRuns is (Decisions Log #64, #140): a CLI cannot see the dispatch
            // handoff window at all, so a count it computed itself would disagree with the one
            // the claims were actually made against.
            ProjectLoads =
            [
                .. load.Projects.Values
                    .Select(project => new ProjectRunLoad(
                        project.ProjectId, project.Ceiling.LiveRuns, project.Ceiling.Cap)),
            ],
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The dispatcher can hold the ceiling; the node can still exceed it. A review park a human
    /// resolved, or a run startup adoption re-entered, hands a worktree back to a session tree
    /// this node had released, and neither path consults the load first. Decisions Log #64
    /// accepts that overshoot on purpose, because refusing a human's explicit resume to protect
    /// a number is the worse trade, and <see cref="NodeLoad.Capacity"/> bounds it by clamping to
    /// zero so nothing further is claimed. Accepted is not the same as invisible: the sweep that
    /// observes the node over its ceiling says so, so the memory pressure that follows has a
    /// recorded cause and not only a symptom. Once per episode, for the same reason a deferral
    /// is logged once — an over-ceiling node stays over for a whole review cycle, and a line
    /// every five seconds would bury everything between them.
    /// </summary>
    private void ReportOverCeiling(NodeLoad load)
    {
        if (load.LiveRuns <= load.MaxConcurrentRuns)
        {
            _reportedOverCeiling = false;
            return;
        }

        if (_reportedOverCeiling)
        {
            return;
        }

        _reportedOverCeiling = true;
        logger.LogWarning(
            "This node is carrying {LiveRuns} live run(s) against a ceiling of {MaxConcurrentRuns} — a resolved "
            + "review park or a run resumed by startup adoption re-entered a session tree this node had "
            + "released. Nothing further is claimed until it is back under the ceiling",
            load.LiveRuns, load.MaxConcurrentRuns);
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
                + "({LiveRuns} of {MaxConcurrentRuns} live run(s)) — it is claimed as a slot frees up",
                taskId, load.LiveRuns, load.MaxConcurrentRuns);
        }

        _deferredClaims.Clear();
        _deferredClaims.UnionWith(deferred);
    }

    /// <summary>
    /// The project cap's own deferral log (Decisions Log #140), on the same one-line-per-episode
    /// discipline <see cref="ReportDeferrals"/> gives the node ceiling and for the same reason —
    /// but as its own line, with its own set, because the two are different limits with different
    /// levers and a queue state must never have to be reconstructed from a line that named the
    /// wrong one. A paused project says so in its own words: "at its cap" and "paused" are the
    /// same mechanism pointed at different problems, and only one of them is answered by raising
    /// a number.
    /// </summary>
    private void ReportProjectCapDeferrals(IReadOnlyCollection<QueuedCandidate> deferred, DispatchLoad load)
    {
        foreach (QueuedCandidate candidate in deferred.Where(candidate => !_deferredByProjectCap.Contains(candidate.TaskId)))
        {
            ProjectLoad project = load.Project(candidate.ProjectId);
            if (project.Ceiling.IsPaused)
            {
                logger.LogInformation(
                    "Task {TaskId} stays queued: project {Project} is paused — its per-project ceiling is 0, so "
                    + "nothing of this project's is claimed however idle this node is. Nothing raises it on its "
                    + "own: h9k project set {Project} --max-parallel-tasks <n>",
                    candidate.TaskId, project.Name, project.Name);
                continue;
            }

            logger.LogInformation(
                project.Ceiling.OverCap
                    ? "Task {TaskId} stays queued: project {Project} is over its own run ceiling (project cap "
                        + "{LiveRuns} running, over a cap of {Cap}) — it is claimed as that project's runs finish"
                    : "Task {TaskId} stays queued: project {Project} is at its own run ceiling (project cap "
                        + "{LiveRuns} of {Cap} live run(s)) — it is claimed as one of that project's runs finishes",
                candidate.TaskId, project.Name, project.Ceiling.LiveRuns, project.Ceiling.Cap);
        }

        _deferredByProjectCap.Clear();
        _deferredByProjectCap.UnionWith(deferred.Select(candidate => candidate.TaskId));
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

        // Belt and suspenders (the same re-validate-right-before-claiming discipline this method
        // already gives the task's own state above): a fresh load on this method's own session,
        // not the archived-project set ReadQueueAsync already filtered the candidate list against
        // — passing that same already-materialized set here (an earlier version of this method
        // did) could never catch a project archived after the queue was read, since a task's own
        // ProjectId never changes and the set was read before this call, making that branch dead
        // code the moment it was written (independent pre-PR review, cycle 1, adversarial lens).
        // This read is against the current database instead, so a project archived between the
        // queue read and this claim is the one case this actually closes. A project with no
        // document at all is a different, pre-existing shape (A_task_whose_project_has_no_document_is_uncapped_rather_than_stuck)
        // and stays uncapped rather than refused here — only an actually-archived project refuses.
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is { IsArchived: true })
        {
            logger.LogWarning(
                "Task {TaskId} is queued under project {ProjectId}, which is now archived — claim refused",
                taskId, task.ProjectId);
            return null;
        }

        if (await PreviousRunStillRunsHereAsync(session, taskId, cancellationToken))
        {
            logger.LogWarning(
                "Task {TaskId} is queued but a previous run's agent process is still alive on this node — claim refused (single-flight per task per node)",
                taskId);
            return null;
        }

        // The project's claim gate, ahead of TaskDecider.Claim (idea 64c75e43): a task linked to
        // a Jira card or a GitHub issue is claimed here only while the tracker shows that item
        // assigned to this install's own tracker identity. A refusal simply returns — the task
        // stays Queued, exactly as the ceiling's and the spend budget's own turned-away tasks do,
        // and nothing about this task's run history records the wait.
        (bool refused, TrackerAssignmentObserved? gateEvidence) =
            await CheckTrackerGateAsync(session, task, cancellationToken);
        if (refused)
        {
            return null;
        }

        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, DateTimeOffset.UtcNow);

        // The gate's own evidence rides ahead of the claim it justified, in the same transaction
        // and under the same expected version, so the stream reads in the order the two things
        // happened and neither can land without the other.
        object[] events = gateEvidence is null ? [claimed] : [gateEvidence, claimed];
        session.Events.Append(taskId, expectedVersion: state.Version + events.Length, events);
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
    /// This project's claim gate, applied to one candidate (idea 64c75e43). Returns whether the
    /// claim is refused and, when it is not, the observation a passing check produced for the
    /// caller to append beside the claim.
    /// <para>
    /// Three things happen here besides the read itself, and all three are what make a hold
    /// legible rather than merely effective: the refusal is published as a
    /// <see cref="TrackerClaimHold"/> so <c>h9k status</c>, <c>h9k task show</c> and
    /// <c>h9k project show</c> can say the same sentence this log line says; it is logged once per
    /// episode rather than once per sweep; and it is re-read no more often than
    /// <see cref="DaemonOptions.PullRequestPollInterval"/> (<see cref="_trackerGateReadAt"/>).
    /// </para>
    /// <para>
    /// A task that stops being gated — the setting turned off, or a reference that changed kind —
    /// has its published hold and its cached read cleared here, so a board never shows a wait that
    /// has already ended.
    /// </para>
    /// <para>
    /// The read itself is caught: an unforeseen failure in a tracker connector must hold the claim
    /// (the gate fails closed, always) rather than throw out of the sweep and take every other
    /// project's claims down with it, the same reasoning <see cref="PublishLoadAsync"/>'s own catch
    /// documents. That hold is published and announced exactly as a concluded one is
    /// (<see cref="TrackerClaimGate.Threw"/>), so all three of the above hold for it too — the
    /// only difference being that its warning carries the exception a status row cannot.
    /// </para>
    /// </summary>
    private async Task<(bool Refused, TrackerAssignmentObserved? Evidence)> CheckTrackerGateAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);

        // The reference is narrowed here rather than re-read from the task further down: Gates()
        // already requires one, and a local carries that through to the failure path without a
        // null-forgiving '!' (AGENTS.md).
        if (project is null
            || task.ExternalReference is not { } item
            || !TrackerClaimGate.Gates(project.ClaimGate, item))
        {
            ReleaseTrackerHold(session, task.Id);
            return (false, null);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_trackerGateReadAt.TryGetValue(task.Id, out DateTimeOffset lastRead)
            && now - lastRead < _options.PullRequestPollInterval)
        {
            return (true, null);
        }

        TrackerClaimDecision decision;
        try
        {
            decision = await _trackerClaimGate.CheckAsync(
                store, project.ClaimGate, item, project.RepositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Published and announced exactly as a concluded hold is, because holding is only half
            // of failing closed: this path used to refuse the claim with nothing written, so
            // h9k status showed the task queued and could give no reason for it — the one hold on
            // this gate a board could not explain (Copilot review, PR #260). The warning keeps the
            // exception, which the hold's own one-line sentence cannot carry, and is said once per
            // episode rather than once per re-read, off the same key ReportTrackerHold uses.
            decision = TrackerClaimGate.Threw(item, exception, now);
            _trackerGateReadAt[task.Id] = now;
            await PublishTrackerHoldAsync(task.Id, decision, cancellationToken);
            if (FirstReportOfTrackerHold(task.Id, decision))
            {
                logger.LogWarning(
                    exception,
                    "Task {TaskId} stays queued: this project's tracker-assignee claim gate could not read "
                    + "{Reference} at all, so it fails closed. The next read is in at most {Interval}",
                    task.Id, item.ToString(), _options.PullRequestPollInterval);
            }

            return (true, null);
        }

        if (decision.Holds)
        {
            _trackerGateReadAt[task.Id] = now;
            await PublishTrackerHoldAsync(task.Id, decision, cancellationToken);
            ReportTrackerHold(task.Id, decision);
            return (true, null);
        }

        ReleaseTrackerHold(session, task.Id);
        return (false, decision.Assignee is { } assignee
            ? new TrackerAssignmentObserved(
                task.Id, item.ToString(), assignee.Identity, assignee.Name, decision.ObservedAt)
            : null);
    }

    /// <summary>
    /// Publish the hold in its own transaction, before the caller returns without claiming
    /// anything. Its own, because the claim session is about to be abandoned unsaved — there is no
    /// claim to commit it alongside — and because a failure to publish must not become a failure
    /// to hold: the gate's refusal has already taken effect by the time this runs, and the worst a
    /// swallowed write costs is one stale line on the board until the next re-read.
    /// </summary>
    private async Task PublishTrackerHoldAsync(
        Guid taskId, TrackerClaimDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession holdSession = store.LightweightSession();
            holdSession.Store(decision.ToHold(taskId, node.NodeId, Environment.MachineName));
            await holdSession.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Task {TaskId} is held by this project's claim gate, but recording why for h9k status failed; "
                + "the hold itself is unaffected",
                taskId);
        }
    }

    /// <summary>
    /// Forget everything this node was holding about a task whose wait has ended — the gate passed,
    /// or stopped applying at all — so a project whose gate was just turned off claims on the very
    /// next sweep with no stale hold left on the board. The delete rides the caller's own session,
    /// which both callers go on to commit with the claim.
    /// <para>
    /// Only a task this node actually recorded a hold for is deleted, so the ordinary ungated
    /// sweep — the overwhelmingly common case, and the one this feature promises to leave
    /// byte-for-byte alone — issues no statement at all rather than a delete for a row that was
    /// never written. The cost is narrow and self-healing: a hold published before a daemon restart,
    /// on a project whose gate was turned off while the daemon was down, is not deleted here — and
    /// every reader freshness-gates it (<c>DispatchPressure.Freshness</c>), so it stops being shown
    /// within minutes on its own.
    /// </para>
    /// </summary>
    private void ReleaseTrackerHold(IDocumentSession session, Guid taskId)
    {
        bool held = _trackerGateReadAt.Remove(taskId) | _reportedTrackerHolds.Remove(taskId);
        if (held)
        {
            // This node's own row and no other's: the key carries the node precisely so a second
            // daemon against the same database keeps its own explanation of the same task
            // (TrackerClaimHold.KeyFor).
            session.Delete<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId));
        }
    }

    /// <summary>
    /// Drop the per-task claim-gate bookkeeping for every task no longer in this node's queue, so
    /// the two dictionaries cannot grow for the life of the process — a card claimed, closed out,
    /// or unassigned would otherwise leave its read time and its reported line behind forever.
    /// Replacement semantics, exactly as <see cref="ReportDeferrals"/> rebuilds its own set each
    /// sweep: a task that leaves the queue and comes back is a fresh hold, re-read on the next
    /// sweep and announced again.
    /// </summary>
    private void ForgetTrackerHoldsOutsideTheQueue(IReadOnlyCollection<QueuedCandidate> queued)
    {
        if (_trackerGateReadAt.Count == 0 && _reportedTrackerHolds.Count == 0)
        {
            return;
        }

        HashSet<Guid> stillQueued = [.. queued.Select(candidate => candidate.TaskId)];
        foreach (Guid taskId in _trackerGateReadAt.Keys.Where(taskId => !stillQueued.Contains(taskId)).ToList())
        {
            _trackerGateReadAt.Remove(taskId);
        }

        foreach (Guid taskId in _reportedTrackerHolds.Keys.Where(taskId => !stillQueued.Contains(taskId)).ToList())
        {
            _reportedTrackerHolds.Remove(taskId);
        }
    }

    /// <summary>
    /// One line per hold, not one per sweep — <see cref="ReportDeferrals"/>'s own discipline. The
    /// full sentence rather than the short reason, because this is the surface where the tracker's
    /// own error and what ends the hold have to be quotable verbatim.
    /// </summary>
    private void ReportTrackerHold(Guid taskId, TrackerClaimDecision decision)
    {
        if (FirstReportOfTrackerHold(taskId, decision))
        {
            logger.LogInformation(
                "Task {TaskId} stays queued: {Reason} It is re-read in at most {Interval}",
                taskId, decision.RefusalLine, _options.PullRequestPollInterval);
        }
    }

    /// <summary>
    /// Whether this hold has not been announced yet, remembering it as announced if so — the
    /// once-per-episode gate the two report paths share, so a hold the tracker's own answer
    /// produced and one an unforeseen failure produced are each said once rather than once per
    /// re-read. Kept apart from <see cref="ReportTrackerHold"/> because the second caller logs a
    /// warning carrying its exception rather than this method's information line, and the keying
    /// must not be duplicated to say that.
    /// </summary>
    private bool FirstReportOfTrackerHold(Guid taskId, TrackerClaimDecision decision)
    {
        if (_reportedTrackerHolds.TryGetValue(taskId, out string? reported) && reported == decision.RefusalLine)
        {
            return false;
        }

        _reportedTrackerHolds[taskId] = decision.RefusalLine;
        return true;
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
