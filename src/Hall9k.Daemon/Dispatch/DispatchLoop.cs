using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Execution;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// The daemon's main loop: wait for Postgres, then adopt → sweep → claim (log #7), then
/// react to the doorbell with a polling sweep as the safety net — NOTIFY is a doorbell,
/// never a payload (log #8).
/// </summary>
public sealed class DispatchLoop(
    IDocumentStore store,
    DaemonConnection connection,
    NodeContext node,
    DispatchEngine engine,
    RunSupervisor supervisor,
    RunLauncher launcher,
    IWorktreeManager worktrees,
    CloseoutEngine closeout,
    IOptions<DaemonOptions> options,
    OperatingSettingsReport concurrencySettings,
    IConfiguration configuration,
    ILogger<DispatchLoop> logger) : BackgroundService
{
    private readonly DaemonOptions _options = options.Value;
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForPostgresAsync(stoppingToken);
        await node.InitializeAsync(store, stoppingToken);
        logger.LogInformation("Node {NodeId} (owner {OwnerId}) starting", node.NodeId, node.OwnerId);

        // The ceiling is stated up front because it is the answer to "why is my queue not
        // moving" (Decisions Log #64, #108), and because it is per-machine configuration: the
        // number this node carries is not the number the next one carries.
        logger.LogInformation(
            "Concurrency ceiling: at most {MaxConcurrentTaskRuns} task run(s) live on this node at once "
            + "(configure with {Section}:{Setting} or h9k config set --max-concurrent-task-runs <n>); the "
            + "per-run session cap defaults to {SessionCapPerRun} and is overridable per task with "
            + "h9k task set-session-cap",
            _options.MaxConcurrentTaskRuns, DaemonOptions.SectionName, nameof(DaemonOptions.MaxConcurrentTaskRuns),
            _options.SessionCapPerRun);
        if (concurrencySettings.MaxConcurrentTaskRunsConvertedFromLegacy)
        {
            logger.LogInformation(
                "{MaxConcurrentTaskRunsSetting} is not set — this node's ceiling of {MaxConcurrentTaskRuns} run(s) "
                + "was converted (floor(sessions/2), minimum 1) from the retired {LegacySetting}, resolved from "
                + "{LegacyOrigin}. Set {MaxConcurrentTaskRunsSetting} directly "
                + "(h9k config set --max-concurrent-task-runs <n>) to stop relying on the conversion",
                nameof(DaemonOptions.MaxConcurrentTaskRuns), _options.MaxConcurrentTaskRuns,
                nameof(DaemonOptions.MaxConcurrentAgentSessions), concurrencySettings.MaxConcurrentTaskRuns.DescribeOrigin(),
                nameof(DaemonOptions.MaxConcurrentTaskRuns));
        }

        WarnAboutRetiredCeilingSetting();

        // Before anything reads the task projections: bring documents written by an older
        // projection shape up to date, or the claim filter cannot see them (log #34).
        await BackfillLifecycleProjectionsAsync(stoppingToken);

        // Startup order matters: reattach before declaring anything dead, requeue the
        // genuinely abandoned, and only then take new work.
        OrphanAdoption adoption = await supervisor.AdoptOrphansAsync(stoppingToken);
        int requeued = await engine.SweepExpiredLeasesAsync(stoppingToken);
        await PruneRegisteredRepositoriesAsync(stoppingToken);
        await ReportCatchUpAsync(adoption, requeued, stoppingToken);

        Task listener = ListenForDoorbellAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Adopt-before-sweep in miniature: a review park resolved by the human
                // (h9k review resolve) or a branch an operator just delivered interactively
                // (h9k task deliver) re-enters the pipeline before anything else acts.
                await supervisor.ResumeStrandedPipelinesAsync(stoppingToken);
                await engine.SweepExpiredLeasesAsync(stoppingToken);
                // Before claiming: whose dependencies have closed out (or died) since last time.
                await engine.ReevaluateBlockedTasksAsync(stoppingToken);
                IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(stoppingToken);
                foreach (ClaimedWork work in claimed)
                {
                    await launcher.LaunchAsync(
                        work.TaskId, work.RunId, node.NodeId, node.OwnerId, work.LeaseGeneration, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Dispatch cycle failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(_options.PollInterval, stoppingToken);
                while (_doorbell.CurrentCount > 0)
                {
                    await _doorbell.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await listener;
    }

    /// <summary>
    /// The startup catch-up report (Decisions Log #31): an immediate closeout sweep —
    /// not waiting for the monitor's first gentle tick — then one log line stating what
    /// happened while the daemon was down. h9k daemon start tails the log for the
    /// marker, so an on-demand daemon's cost is visibly latency, never correctness
    /// (the #29 lesson: down is not death).
    /// </summary>
    private async Task ReportCatchUpAsync(OrphanAdoption adoption, int requeuedLeases, CancellationToken cancellationToken)
    {
        string closeoutSummary;
        try
        {
            CloseoutSweepResult sweep = await closeout.PollOnceAsync(cancellationToken);
            closeoutSummary =
                $"closeout sweep inspected {sweep.RunsInspected} pull request(s) and observed {sweep.MergesObserved} merge(s)";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The sweep needs gh and the network, both often absent right after a wake
            // or a boot; the monitor retries on its normal cadence.
            closeoutSummary = $"closeout sweep failed ({exception.Message}); the monitor retries on its normal cadence";
        }

        logger.LogInformation(
            "{Marker} — adopted {Adopted} run(s), failed {Failed} orphaned run(s), requeued {Requeued} expired lease(s); {Closeout}",
            DaemonRuntime.CatchUpMarker, adoption.RunsAdopted, adoption.RunsFailed, requeuedLeases, closeoutSummary);
    }

    /// <summary>
    /// Say out loud that a configured <c>MaxConcurrentRuns</c> — the original, pre-#64 key, not
    /// <see cref="DaemonOptions.MaxConcurrentAgentSessions"/> — is no longer read. Originally
    /// (Decisions Log #64) the ceiling moved from this run-denominated key to the session-
    /// denominated <c>MaxConcurrentAgentSessions</c>, so the old number could not be carried over
    /// as an alias without silently meaning something else — a tower told to run four would get
    /// four sessions, which is one or two runs. Decisions Log #108 has since moved the ceiling back
    /// to a run-denominated key, <see cref="DaemonOptions.MaxConcurrentTaskRuns"/>, which this
    /// retired key's own value would in fact carry over unchanged — but binding it quietly would
    /// still be a guess about intent (AGENTS.md: never guess at unobserved facts): the two
    /// denominations only coincide again today, and an operator who set this key and forgot about
    /// it should not have that guess made silently on a future re-denomination either. Binding it
    /// quietly would be a guess about intent, and dropping it quietly would leave an operator
    /// watching a ceiling they thought they had raised. So it is dropped out loud.
    /// </summary>
    private void WarnAboutRetiredCeilingSetting()
    {
        const string retired = "MaxConcurrentRuns";
        if (string.IsNullOrWhiteSpace(configuration.GetSection(DaemonOptions.SectionName)[retired]))
        {
            return;
        }

        logger.LogWarning(
            "{RetiredSetting} is set but is no longer read — the ceiling is now configured directly "
            + "in task runs as {Section}:{Setting} (h9k config set --max-concurrent-task-runs <n>), "
            + "so the old value is not carried over. This node runs on the ceiling logged above "
            + "until you set the new one",
            $"{DaemonOptions.SectionName}:{retired}",
            DaemonOptions.SectionName, nameof(DaemonOptions.MaxConcurrentTaskRuns));
    }

    private async Task WaitForPostgresAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using NpgsqlConnection connection = new(ConnectionString());
                await connection.OpenAsync(cancellationToken);
                logger.LogInformation("Postgres reachable");
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Postgres not reachable yet ({Message}); retrying in {Delay}s",
                    exception.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task ListenForDoorbellAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection connection = new(ConnectionString());
                await connection.OpenAsync(cancellationToken);
                connection.Notification += (_, args) =>
                {
                    logger.LogDebug("Doorbell: {Payload}", args.Payload);
                    _doorbell.Release();
                };

                await using (NpgsqlCommand listen = new("LISTEN hall9k", connection))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                logger.LogInformation("Listening on channel 'hall9k'");
                while (!cancellationToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Doorbell listener dropped; reconnecting in 5s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The migration every task-projection shape change needs (Decisions Log #34, #61), run at
    /// startup because the dispatcher and the attention pane are the things that break without
    /// it. A failure is logged rather than fatal: taking the daemon down would stop the tasks
    /// that are fine along with the ones that are stale, and the next start tries again.
    /// </summary>
    private async Task BackfillLifecycleProjectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(store, cancellationToken);
            if (rebuilt.Count > 0)
            {
                logger.LogInformation(
                    "Re-projected {Count} task(s) whose documents were written before a projection "
                    + "shape change — an absent field reads as nothing recorded, which is how a task "
                    + "becomes unclaimable or loses the reason it is held for",
                    rebuilt.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Re-projecting out-of-date task documents failed. Tasks last projected before a "
                + "projection shape change will misread until this succeeds; the next daemon start "
                + "retries it");
        }
    }

    private async Task PruneRegisteredRepositoriesAsync(CancellationToken cancellationToken)
    {
        await using Marten.IQuerySession query = store.QuerySession();
        IReadOnlyList<ProjectDetails> projects = await query.Query<ProjectDetails>().ToListAsync(cancellationToken);
        foreach (ProjectDetails project in projects)
        {
            try
            {
                await worktrees.PruneAsync(project.RepositoryPath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Worktree prune failed for {Repository}", project.RepositoryPath);
            }
        }
    }

    private string ConnectionString() => connection.ConnectionString;
}
