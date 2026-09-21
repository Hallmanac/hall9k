using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.LocalLaunches;

/// <summary>
/// The heartbeat behind <see cref="LocalLaunchSweepEngine"/> (idea b9b09779, piece 5). Waits for
/// this node's own identity before its first tick, because the sweep only acts on launches this
/// node started and cannot tell which those are until it knows who it is.
/// </summary>
public sealed class LocalLaunchSweepLoop(
    LocalLaunchSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options,
    ILogger<LocalLaunchSweepLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int stopped = await engine.SweepOnceAsync(stoppingToken);
                if (stopped > 0)
                {
                    logger.LogInformation("Local-launch sweep: {Stopped} launch(es) torn down", stopped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Local-launch sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.LocalLaunchSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
