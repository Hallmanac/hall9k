using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>
/// The heartbeat behind backlog 48: a doorbell-woken, poll-backstopped sweep over every project
/// home on this machine (the same shape as <see cref="Hall9k.Daemon.Publication.CardPublicationLoop"/>).
/// The first sweep runs before this loop ever waits on anything, which is what makes it double as
/// the daemon-start reconciliation pass the acceptance criteria ask for: there is nothing special
/// about "the first run" beyond it happening to run before any wait.
/// </summary>
public sealed class ProjectHomeRenderLoop(
    ProjectHomeRenderEngine engine,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<ProjectHomeRenderLoop> logger) : BackgroundService
{
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task listener = ListenForDoorbellAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProjectHomeRenderSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                if (sweep.TasksRendered > 0 || sweep.IdeasRendered > 0 || sweep.OrphansHandled > 0)
                {
                    logger.LogInformation(
                        "Project home render: {Tasks} task file(s), {Ideas} idea file(s) written across "
                        + "{Projects} home(s) on this machine; {Orphans} orphaned director(y/ies) handled",
                        sweep.TasksRendered, sweep.IdeasRendered, sweep.ProjectsInspected, sweep.OrphansHandled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Project home render sweep failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(options.Value.ProjectHomeRenderPollInterval, stoppingToken);
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
    /// NOTIFY carries no payload (log #8): every write on the shared "hall9k" channel wakes this
    /// loop too, and a sweep that finds nothing new to render costs one query per project. Not
    /// every task or idea command rings the doorbell today (a draft revise does not, since nothing
    /// dispatches from it) — the poll interval is the backstop that catches those within a bounded
    /// wait rather than this loop depending on every write path remembering to ring it.
    /// </summary>
    private async Task ListenForDoorbellAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection connectionHandle = new(connection.ConnectionString);
                await connectionHandle.OpenAsync(cancellationToken);
                connectionHandle.Notification += (_, _) => _doorbell.Release();

                await using (NpgsqlCommand listen = new($"LISTEN {Doorbell.Channel}", connectionHandle))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    await connectionHandle.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Project home render doorbell listener dropped; reconnecting in 5s");
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
}
