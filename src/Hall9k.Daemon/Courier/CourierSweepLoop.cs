using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Courier;

/// <summary>
/// The heartbeat behind <see cref="CourierEngine"/> (idea 89471598, piece 3). Waits for this
/// node's own identity before its first tick, the same reason
/// <c>OrchestratorPresenceSweepLoop</c> does: the sweep is scoped to this node's own registered
/// orchestrator windows and has nothing to scope to until bootstrap has run.
/// </summary>
public sealed class CourierSweepLoop(
    CourierEngine engine, NodeContext node, IOptions<DaemonOptions> options, ILogger<CourierSweepLoop> logger)
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
                CourierSweepResult result = await engine.SweepOnceAsync(stoppingToken);
                if (result.Delivered > 0 || result.Failed > 0 || result.NoAdapter > 0 || result.DayCapHits > 0)
                {
                    logger.LogInformation(
                        "Feed courier sweep: {Delivered} delivered, {Failed} did not deliver, "
                        + "{NoAdapter} skipped for no fitting adapter, {DayCapHits} at the day cap",
                        result.Delivered, result.Failed, result.NoAdapter, result.DayCapHits);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Feed courier sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.CourierSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
