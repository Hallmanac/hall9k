using Hall9k.Connectors.Orchestrator;
using Hall9k.Connectors.Replication;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Courier;

/// <summary>One sweep's tally, for the loop's own log line.</summary>
public sealed record CourierSweepResult(int Delivered, int Failed, int NoAdapter, int DayCapHits);

/// <summary>
/// The feed courier's own spawn engine (idea 89471598, piece 3): every project this node has ever
/// registered an orchestrator window against, checked once a tick against the four conditions
/// <see cref="CourierGate"/> decides — undrained items, a live orchestrator, no courier already
/// running, and the batching wait elapsed — and spawned, waited on, and recorded exactly the way
/// <c>CardPublicationEngine</c> spawns its own task-optional auxiliary session, minus everything
/// that class needs and this one does not: no worktree, no task, no recipe.
/// </summary>
public sealed class CourierEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    IOrchestratorProcessProbe probe,
    IOptions<DaemonOptions> options,
    ILogger<CourierEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    public async Task<CourierSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        IReadOnlyList<OrchestratorPresenceDetails> presences;
        await using (IQuerySession query = store.QuerySession())
        {
            // Registered=false is a window this node once saw and no longer has: the presence
            // sweep never deletes the row, only appends OrchestratorLost against it, so it would
            // otherwise sit here forever, paying every tick's own cheap checks for a window that
            // is provably never coming back. Filtered here rather than left to TickAsync's own
            // orchestratorLive check, which still exists for the case that check alone cannot
            // rule out: a window registered on this node whose process has since died without the
            // presence sweep having caught up to it yet.
            presences = await query.Query<OrchestratorPresenceDetails>()
                .Where(presence => presence.NodeId == nodeId && presence.Registered)
                .ToListAsync(cancellationToken);
        }

        // Ticked concurrently rather than one project's turn at a time: TickAsync's own spawn path
        // blocks on SessionResultWaiter for up to CourierTimeout (three minutes) once it decides to
        // spawn, and a serial loop would let one slow or hung project's courier delay every other
        // project's tick behind it in the list — including an urgent item on one of them, whose
        // whole point is bypassing the batching wait to reach a live orchestrator "at once". Each
        // tick opens its own session and touches only its own project's documents, so nothing here
        // is shared state a concurrent run could race.
        CourierTickOutcome[] outcomes = await Task.WhenAll(
            presences.Select(presence => TickAsync(presence, cancellationToken)));

        int delivered = 0;
        int failed = 0;
        int noAdapter = 0;
        int dayCapHits = 0;
        foreach (CourierTickOutcome outcome in outcomes)
        {
            switch (outcome)
            {
                case CourierTickOutcome.Delivered:
                    delivered++;
                    break;
                case CourierTickOutcome.Failed:
                    failed++;
                    break;
                case CourierTickOutcome.NoAdapter:
                    noAdapter++;
                    break;
                case CourierTickOutcome.DayCapHit:
                    dayCapHits++;
                    break;
                case CourierTickOutcome.NotThisTick:
                default:
                    break;
            }
        }

        return new CourierSweepResult(delivered, failed, noAdapter, dayCapHits);
    }

    /// <summary>What one project's own tick came to, for the sweep's own tally — never persisted, the same in-process-outcome idiom <c>CardPublicationEngine.PublicationAttempt</c> already uses.</summary>
    private enum CourierTickOutcome
    {
        NotThisTick,
        Delivered,
        Failed,
        NoAdapter,
        DayCapHit,
    }

    /// <summary>
    /// How far past a courier's own <see cref="DaemonOptions.CourierTimeout"/> a run with no
    /// recorded outcome must stand before <see cref="TickAsync"/> treats it as stranded rather
    /// than still running. <see cref="SpawnAsync"/> bounds its own wait to that timeout and
    /// records an outcome on every path out of it now (a delivery, a failure, a timeout, or a
    /// shutdown mid-flight) — the only way <see cref="CourierRunDetails.CompletedAt"/> can still
    /// be null this far past dispatch is a daemon that stopped existing outright (a kill -9, a
    /// host power loss) before any of those paths ran. The margin is slack for save latency
    /// alone, not a second budget for the session itself.
    /// </summary>
    private static readonly TimeSpan StrandedRunGrace = TimeSpan.FromMinutes(1);

    private async Task<CourierTickOutcome> TickAsync(
        OrchestratorPresenceDetails presence, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(presence.ProjectId, cancellationToken);
        if (project is null || project.IsArchived)
        {
            // A project purged or archived since this node last registered a window against it:
            // nothing here for a courier to deliver to any more.
            return CourierTickOutcome.NotThisTick;
        }

        bool orchestratorLive = presence.Registered
            && OrchestratorLiveness.IsStillRunning(presence.ProcessId, presence.ProcessStartedAt, probe);
        if (!orchestratorLive)
        {
            // CourierGate.Decide refuses the instant it sees no live orchestrator regardless of
            // what the feed holds, so the expensive full-history scan below (independent pre-PR
            // review, conformance lens) is never worth paying for a project nobody is watching
            // right now — every stale, long-since-closed presence this node has ever registered a
            // window for pays this same cheap check and nothing more.
            return CourierTickOutcome.NotThisTick;
        }

        CourierRunDetails? lastRun = await session.Query<CourierRunDetails>()
            .Where(run => run.ProjectId == project.Id)
            .OrderByDescending(run => run.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        bool courierAlreadyRunning = false;
        if (lastRun is { CompletedAt: null } running)
        {
            if (now - running.DispatchedAt > _options.CourierTimeout + StrandedRunGrace)
            {
                // Stranded: SpawnAsync's own wait is bounded to CourierTimeout and every path out
                // of it now records an outcome, so a run still unfinished this far past dispatch
                // means the daemon that dispatched it stopped existing before any of those paths
                // could run. Adopted here as failed rather than left to block every future
                // courier for this project forever (RecordOutcomeAsync's own doc names exactly
                // this hazard; independent pre-PR review, conformance and adversarial lenses).
                logger.LogWarning(
                    "Project {ProjectName}: the feed courier dispatched at {DispatchedAt:u} never "
                    + "recorded an outcome and has stood past its own timeout ({Timeout}) — marking it "
                    + "failed so future couriers for this project are not blocked",
                    project.Name, running.DispatchedAt, _options.CourierTimeout);
                await RecordOutcomeAsync(
                    store, project.Id, running.Id, AgentModel.Unknown, delivered: false,
                    "The daemon that dispatched this courier never recorded how it ended (a stop, a "
                    + "crash, or an unclean shutdown); marked failed after it stood past its own "
                    + "timeout so future couriers for this project are not blocked.",
                    drainableThroughSequence: 0, result: null, now, cancellationToken);

                // running is the same object lastRun points at, and RecordOutcomeAsync just wrote
                // this completion to the store — mutated here so the elapsedSinceLastCourier and
                // lastCourierFailed reads below see it too, rather than the stale CompletedAt:
                // null this local copy was fetched with before the adoption above. Left stale, the
                // gate reads a null elapsedSinceLastCourier and spawns a fresh courier in this same
                // tick with none of the lastCourierFailed backoff a normal failure gets
                // (independent pre-PR review, adversarial lens).
                running.CompletedAt = now;
                running.Delivered = false;
            }
            else
            {
                courierAlreadyRunning = true;
            }
        }

        if (courierAlreadyRunning)
        {
            return CourierTickOutcome.NotThisTick;
        }

        OrchestratorFeedDrainLease? lease =
            await session.LoadAsync<OrchestratorFeedDrainLease>(project.Id, cancellationToken);
        bool manualDrainLeaseHeld = OrchestratorFeedDrainLease.IsHeld(lease, now);
        if (manualDrainLeaseHeld)
        {
            return CourierTickOutcome.NotThisTick;
        }

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        CourierDaySpawnCounter? dayCounter =
            await session.LoadAsync<CourierDaySpawnCounter>(project.Id, cancellationToken);
        int spawnsToday = CourierDaySpawnCounter.CountFor(dayCounter, today);

        TimeSpan? elapsedSinceLastCourier = lastRun?.CompletedAt is { } lastCompletedAt
            ? now - lastCompletedAt
            : null;
        bool lastCourierFailed = lastRun is { CompletedAt: not null, Delivered: false };

        // Reached only once every cheap, single-document check above has already passed: the full
        // per-project history scan (independent pre-PR review, conformance lens) is the one
        // genuinely expensive step in a tick, and every project without a live orchestrator, a
        // courier already running, or a manual drain in progress now never pays it.
        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        OrchestratorFeedRead read = await reader.ReadUndrainedAsync(
            session, project.Id, project.OrchestratorFeed, now, cancellationToken);

        if (read.Items.Count == 0 && read.ScanWasCapped)
        {
            // Nothing at all was admitted out of this capped scan — not even an item still inside
            // the settling window, which read.Items would still carry if there were one — so every
            // event up to read.DrainableThroughSequence has been considered once and rejected, and
            // advancing the cursor there cannot drop anything. Left undrained here, a project whose
            // own events are outnumbered past the scan's own cap (OrchestratorFeedReader.MaxEventsPerRead)
            // by another project's ordinary traffic on this node would have its cursor frozen
            // forever: every future tick re-reads the identical noise-only window and never reaches
            // the real item waiting past it (independent pre-PR review, cycle 4, conformance lens).
            await OrchestratorFeedReader.DrainAsync(
                session, project.Id, read.DrainableThroughSequence, now, cancellationToken);
        }

        // Items newer than the settling window are printed but not yet safe to drain
        // (OrchestratorFeedRead's own doc): delivering one now and finding the drain made no
        // progress would hand the identical item back on the very next tick — an urgent one twice
        // before it had even settled (independent pre-PR review, adversarial lens). The gate and
        // the prompt both act on this narrower, actually-drainable set instead.
        IReadOnlyList<OrchestratorFeedItem> deliverableItems =
            [.. read.Items.Where(item => item.Sequence <= read.DrainableThroughSequence)];
        OrchestratorFeedRead deliverableRead = read with { Items = deliverableItems };

        // The newest pending item's own age is the "how recently did something new arrive" signal
        // CourierGate.Wait ramps against, read off the full set on purpose (not the drainable-only
        // one above): a fresh burst should hold the batching wait open even before it settles.
        // read.Items is oldest-first (OrchestratorFeedSelection's own construction order), so the
        // last entry is the newest one.
        TimeSpan quietFor = TimeSpan.Zero;
        if (read.Items.Count > 0)
        {
            TimeSpan elapsed = now - read.Items[^1].At;
            quietFor = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }

        CourierSpawnDecision decision = CourierGate.Decide(
            hasUndrainedItems: deliverableRead.Items.Count > 0,
            orchestratorLive,
            courierAlreadyRunning,
            manualDrainLeaseHeld,
            hasUrgentItem: deliverableRead.HasUrgentItem,
            lastCourierFailed,
            elapsedSinceLastCourier,
            quietFor,
            _options.CourierQuietThreshold,
            maxWait: TimeSpan.FromSeconds(project.CourierMaxWaitSeconds ?? ProjectAggregate.DefaultCourierMaxWaitSeconds),
            spawnsToday,
            _options.CourierDaySpawnCap);

        if (decision == CourierSpawnDecision.DayCapReached)
        {
            if (dayCounter is not { CapHitLogged: true })
            {
                logger.LogWarning(
                    "Project {ProjectName}: the feed courier's own per-day spawn cap ({Cap}) is spent; "
                    + "no more couriers will spawn for this project until the day rolls",
                    project.Name, _options.CourierDaySpawnCap);
                session.Store(dayCounter is null
                    ? new CourierDaySpawnCounter { Id = project.Id, Day = today, Count = spawnsToday, CapHitLogged = true }
                    : new CourierDaySpawnCounter { Id = project.Id, Day = dayCounter.Day, Count = dayCounter.Count, CapHitLogged = true });
                await session.SaveChangesAsync(cancellationToken);
            }

            return CourierTickOutcome.DayCapHit;
        }

        if (decision != CourierSpawnDecision.Spawn)
        {
            return CourierTickOutcome.NotThisTick;
        }

        ICourierDeliveryAdapter? adapter = CourierDeliveryAdapterRegistry.ForCli(presence.Cli);
        if (adapter is null)
        {
            logger.LogWarning(
                "Project {ProjectName}: no delivery adapter fits the orchestrator's own CLI ('{Cli}') — "
                + "the feed stays undrained until one does",
                project.Name, presence.Cli);
            return CourierTickOutcome.NoAdapter;
        }

        return await SpawnAsync(
            session, project, presence, adapter, deliverableRead, now, today, dayCounter, cancellationToken);
    }

    private async Task<CourierTickOutcome> SpawnAsync(
        IDocumentSession session,
        ProjectDetails project,
        OrchestratorPresenceDetails presence,
        ICourierDeliveryAdapter adapter,
        OrchestratorFeedRead read,
        DateTimeOffset now,
        DateOnly today,
        CourierDaySpawnCounter? dayCounter,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> feedLines =
            await OrchestratorFeedPrinter.ComposeAsync(session, read, TimeZoneInfo.Local, cancellationToken);
        string prompt = CourierPromptBuilder.Build(
            project.Name, feedLines, adapter.BuildDeliveryInstruction(presence.SessionName));
        AgentModel model = _options.ResolveCourierModel(project.Model);
        Guid runId = DomainId.New();

        session.Events.StartStream(runId, new CourierRunDispatched(runId, project.Id, node.NodeId, model, now));
        session.Store(CourierDaySpawnCounter.Incremented(dayCounter, project.Id, today));
        await session.SaveChangesAsync(cancellationToken);

        // Everything from here on is recorded, on every path out, before this method returns or
        // rethrows: the dispatch just above is already committed, so a path that leaves without
        // ever appending CourierRunCompleted is exactly the permanent-block hazard
        // RecordOutcomeAsync's own doc names (independent pre-PR review, conformance and
        // adversarial lenses) — TickAsync's own stranded-run adoption only covers a daemon that
        // stops existing outright; every path that runs at all closes the rest of that gap here.
        SpawnedAgent? agent = null;
        AgentResult? result = null;
        bool delivered;
        string outcome;
        try
        {
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    runId, runId, RunPaths.GlobalDirectory(runId), RunPaths.GlobalDirectory(runId), prompt,
                    ExecutorMode.Subscription, model, project.SkipPermissions,
                    // No recipe and no AGENTS.md (the acceptance criteria's own wording): dropping the
                    // checkout-scoped settings and doctrine files is exactly what this flag already
                    // does for a pull request's own untrusted head, and a courier's own artifact
                    // directory carries neither anyway — belt and suspenders.
                    UntrustedWorkingDirectory: true,
                    MaxTurns: _options.CourierMaxTurns)
                {
                    SessionName = SessionRoleName.For(DomainId.Short(project.Id), SessionRoleName.Courier),
                },
                cancellationToken);

            logger.LogInformation(
                "Project {ProjectName}: feed courier dispatched (pid {ProcessId}, model {Model}, {ItemCount} item(s))",
                project.Name, agent.ProcessId, model.Value, read.Items.Count);

            bool timedOut = false;
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.CourierTimeout);
            try
            {
                result = (await SessionResultWaiter.WaitAsync(
                    RunPaths.StreamFile(RunPaths.GlobalDirectory(runId)), agent.ProcessId, agent.StartedAt,
                    processManager, onOutput: null, budget.Token)).Result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Project {ProjectName}: the feed courier exceeded {Timeout} — terminating it",
                    project.Name, _options.CourierTimeout);
                TerminateQuietly(project.Name, agent);
                timedOut = true;
                result = null;
            }

            (delivered, outcome) = CourierDeliveryOutcome.Parse(result, timedOut, _options.CourierTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The daemon is stopping. Whatever was started is stopped with it, and the outcome is
            // still recorded — with a token of its own, since cancellationToken is already
            // cancelled and cannot be used to save — so this run never reads as "still running"
            // forever. CardPublicationEngine.StopForShutdownAsync is the identical shape for the
            // sibling auxiliary session (independent pre-PR review, conformance lens).
            if (agent is { } started)
            {
                TerminateQuietly(project.Name, started);
            }

            await RecordShutdownOutcomeAsync(project.Name, project.Id, runId, model);
            throw;
        }
        catch (Exception exception)
        {
            // Nothing else here is expected to throw — executor.SpawnAsync failing (a missing
            // binary, a spawn failure) or something unexpected escaping the wait above — but the
            // dispatch is already committed, so leaving this run's own outcome unrecorded strands
            // it the same way an uncaught shutdown cancellation would (independent pre-PR review,
            // conformance and adversarial lenses). The sweep loop's own catch-all still logs and
            // retries the *next* tick; this is what keeps this run from blocking every tick after it.
            logger.LogError(
                exception,
                "Project {ProjectName}: the feed courier could not be seen through to an outcome; "
                + "recording it as failed",
                project.Name);
            if (agent is { } started)
            {
                TerminateQuietly(project.Name, started);
            }

            delivered = false;
            outcome = $"The daemon could not see this courier through to an outcome: {exception.Message}";
        }

        await RecordOutcomeAsync(
            store, project.Id, runId, model, delivered, outcome, read.DrainableThroughSequence, result,
            DateTimeOffset.UtcNow, cancellationToken);

        logger.LogInformation(
            "Project {ProjectName}: feed courier {Outcome}",
            project.Name, delivered ? "delivered" : "did not deliver");

        return delivered ? CourierTickOutcome.Delivered : CourierTickOutcome.Failed;
    }

    /// <summary>
    /// How long the shutdown path gets to record an outcome — mirrors
    /// <c>CardPublicationEngine.ShutdownRecordTimeout</c> for the identical reason: this runs
    /// while the daemon is stopping, and a stop that waits on Postgres is worse than an outcome
    /// <see cref="TickAsync"/>'s own stranded-run adoption records on a later restart instead.
    /// </summary>
    private static readonly TimeSpan ShutdownRecordTimeout = TimeSpan.FromSeconds(5);

    private async Task RecordShutdownOutcomeAsync(
        string projectName, Guid projectId, Guid runId, AgentModel model)
    {
        try
        {
            using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
            await RecordOutcomeAsync(
                store, projectId, runId, model, delivered: false,
                "The daemon stopped while this courier was running, and it was stopped with it.",
                drainableThroughSequence: 0, result: null, DateTimeOffset.UtcNow, shutdown.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Project {ProjectName}: could not record the feed courier's outcome during shutdown; "
                + "it will be adopted as stranded once it stands past its own timeout",
                projectName);
        }
    }

    private void TerminateQuietly(string projectName, SpawnedAgent agent)
    {
        try
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Project {ProjectName}: could not terminate the feed courier (pid {ProcessId})",
                projectName, agent.ProcessId);
        }
    }

    /// <summary>
    /// Everything a courier's own ending writes, pulled out of <see cref="SpawnAsync"/> so a test
    /// can drive it directly against a real store without simulating a spawned process at all —
    /// the acceptance criterion this satisfies is "delivery success advances the cursor, failure
    /// leaves it", which is a fact about this method's own two branches, not about
    /// <see cref="SessionResultWaiter"/>'s file-tailing.
    /// </summary>
    internal static async Task RecordOutcomeAsync(
        IDocumentStore store,
        Guid projectId,
        Guid runId,
        AgentModel model,
        bool delivered,
        string outcome,
        long drainableThroughSequence,
        AgentResult? result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        // Recorded before the drain below, deliberately: OrchestratorFeedReader.DrainAsync
        // commits in its own SaveChangesAsync, so the two cannot land in one transaction. A crash
        // between them then leaves the run correctly marked completed either way, never stuck at
        // "still running" (which would block every future courier for this project forever) —
        // the worst it can leave behind is a delivered courier whose cursor never advanced, which
        // simply redelivers the identical items next tick, the same as a plain re-read of this
        // feed always could.
        await using (IDocumentSession completion = store.LightweightSession())
        {
            completion.Events.Append(runId, new CourierRunCompleted(runId, delivered, outcome, completedAt));
            if (result is not null)
            {
                completion.Events.Append(runId, result.ToCourierTokensRecorded(runId, completedAt, model));
            }

            await completion.SaveChangesAsync(cancellationToken);
        }

        if (delivered)
        {
            // The sequence captured before this courier was ever spawned — never re-read from a
            // fresh feed query — so an item that arrived after the prompt was already composed is
            // never drained on the strength of a delivery that never carried it.
            await using IDocumentSession drain = store.LightweightSession();
            await OrchestratorFeedReader.DrainAsync(
                drain, projectId, drainableThroughSequence, completedAt, cancellationToken);
        }
    }
}
