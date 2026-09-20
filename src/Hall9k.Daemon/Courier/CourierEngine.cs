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
            presences = await query.Query<OrchestratorPresenceDetails>()
                .Where(presence => presence.NodeId == nodeId)
                .ToListAsync(cancellationToken);
        }

        int delivered = 0;
        int failed = 0;
        int noAdapter = 0;
        int dayCapHits = 0;
        foreach (OrchestratorPresenceDetails presence in presences)
        {
            switch (await TickAsync(presence, cancellationToken))
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

        OrchestratorFeedDrainLease? lease =
            await session.LoadAsync<OrchestratorFeedDrainLease>(project.Id, cancellationToken);
        bool manualDrainLeaseHeld = OrchestratorFeedDrainLease.IsHeld(lease, now);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        OrchestratorFeedRead read = await reader.ReadUndrainedAsync(
            session, project.Id, project.OrchestratorFeed, now, cancellationToken);

        CourierRunDetails? lastRun = await session.Query<CourierRunDetails>()
            .Where(run => run.ProjectId == project.Id)
            .OrderByDescending(run => run.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        bool courierAlreadyRunning = lastRun is { CompletedAt: null };
        TimeSpan? elapsedSinceLastCourier = lastRun?.CompletedAt is { } lastCompletedAt
            ? now - lastCompletedAt
            : null;

        // The newest pending item's own age is the "how recently did something new arrive"
        // signal CourierGate.Wait ramps against: read.Items is oldest-first (OrchestratorFeedSelection's
        // own construction order), so the last entry is the newest one.
        TimeSpan quietFor = TimeSpan.Zero;
        if (read.Items.Count > 0)
        {
            TimeSpan elapsed = now - read.Items[^1].At;
            quietFor = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        CourierDaySpawnCounter? dayCounter =
            await session.LoadAsync<CourierDaySpawnCounter>(project.Id, cancellationToken);
        int spawnsToday = CourierDaySpawnCounter.CountFor(dayCounter, today);

        CourierSpawnDecision decision = CourierGate.Decide(
            hasUndrainedItems: read.Items.Count > 0,
            orchestratorLive,
            courierAlreadyRunning,
            manualDrainLeaseHeld,
            hasUrgentItem: read.HasUrgentItem,
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

        return await SpawnAsync(session, project, presence, adapter, read, now, today, dayCounter, cancellationToken);
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

        SpawnedAgent agent = await executor.SpawnAsync(
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

        AgentResult? result;
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
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
            timedOut = true;
            result = null;
        }
        catch (OperationCanceledException)
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
            throw;
        }

        (bool delivered, string outcome) =
            CourierDeliveryOutcome.Parse(result, timedOut, _options.CourierTimeout);

        await RecordOutcomeAsync(
            store, project.Id, runId, model, delivered, outcome, read.DrainableThroughSequence, result,
            DateTimeOffset.UtcNow, cancellationToken);

        logger.LogInformation(
            "Project {ProjectName}: feed courier {Outcome}",
            project.Name, delivered ? "delivered" : "did not deliver");

        return delivered ? CourierTickOutcome.Delivered : CourierTickOutcome.Failed;
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
