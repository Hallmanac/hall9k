using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Purge;

/// <summary>
/// The heartbeat behind a project's own scheduled destruction (task: an archived project can be
/// purged). The first sweep runs before this loop ever waits on anything — the same shape
/// <c>ProjectHomeRenderLoop</c> uses — which is what makes it double as the daemon-start
/// reconciliation pass a purge whose deadline passed while the daemon was down needs: a due purge
/// fires on the very next start rather than never, with no special "was I down" case anywhere in
/// this loop or <see cref="ProjectPurgeEngine"/>.
/// </summary>
public sealed class ProjectPurgeSweepLoop(
    ProjectPurgeEngine engine, IOptions<DaemonOptions> options, ILogger<ProjectPurgeSweepLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProjectPurgeSweepResult sweep = await engine.SweepOnceAsync(stoppingToken);
                if (sweep.ProjectsPurged > 0 || sweep.Failures > 0)
                {
                    logger.LogInformation(
                        "Project purge sweep: {Projects} project(s) purged, destroying {Tasks} task(s), "
                        + "{Runs} run(s), {Ideas} idea(s); {Failures} failed and will retry next sweep",
                        sweep.ProjectsPurged, sweep.TasksDestroyed, sweep.RunsDestroyed, sweep.IdeasDestroyed,
                        sweep.Failures);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Project purge sweep failed; will retry next sweep");
            }

            try
            {
                await Task.Delay(options.Value.ProjectPurgeSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
