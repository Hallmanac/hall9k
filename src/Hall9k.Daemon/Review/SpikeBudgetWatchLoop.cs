using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Polls <see cref="SpikeEngine.EndRunsOverWallClockBudgetAsync"/> on the ordinary sweep cadence
/// (task: TaskConstraints gains its first consumer), the identical shape
/// <c>Hall9k.Daemon.Execution.TakeoverWatchLoop</c> already uses for its own continuous,
/// doorbell-free live-run supervision: a spike's stated wall-clock budget has nothing to do with
/// claiming new work, so its own hosted service rather than a step folded into the dispatch loop.
/// </summary>
public sealed class SpikeBudgetWatchLoop(
    SpikeEngine spike,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<SpikeBudgetWatchLoop> logger) : BackgroundService
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
                await spike.EndRunsOverWallClockBudgetAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Spike budget watch sweep failed; will check again next tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
