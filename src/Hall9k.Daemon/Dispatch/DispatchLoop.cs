using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
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
    ILogger<DispatchLoop> logger) : BackgroundService
{
    private readonly DaemonOptions _options = options.Value;
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForPostgresAsync(stoppingToken);
        await node.InitializeAsync(store, stoppingToken);
        logger.LogInformation("Node {NodeId} (owner {OwnerId}) starting", node.NodeId, node.OwnerId);

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
                // (h9k review resolve) re-enters the pipeline before anything else acts.
                await supervisor.ResumeResolvedReviewsAsync(stoppingToken);
                await engine.SweepExpiredLeasesAsync(stoppingToken);
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
