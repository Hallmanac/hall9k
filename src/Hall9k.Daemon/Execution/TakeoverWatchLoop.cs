using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Polls <see cref="RunSupervisor.StopRunsSupersededByTakeoverAsync"/> on the ordinary sweep
/// cadence (idea 202383dc, item 4) — its own hosted service rather than a step folded into the
/// dispatch loop, the same reasoning <see cref="LaunchHoldMonitor"/>'s own doc gives for keeping
/// its own probe separate: this node's live runs are supervised continuously, not only once per
/// dispatch cycle, and this check has nothing to do with claiming new work. No doorbell either —
/// nothing on this node observes "a forced takeover just replicated in", so a short poll
/// (<see cref="DaemonOptions.PollInterval"/>) is the whole mechanism, exactly as
/// <c>TokenBudgetRetryMonitor</c>'s own doc states for its very different clock.
/// </summary>
public sealed class TakeoverWatchLoop(
    RunSupervisor supervisor,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<TakeoverWatchLoop> logger) : BackgroundService
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
                await supervisor.StopRunsSupersededByTakeoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Takeover-watch sweep failed; will check again next tick");
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
