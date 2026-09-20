using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Orchestrators;

/// <summary>
/// The heartbeat behind <see cref="OrchestratorPresenceSweepEngine"/> (idea 89471598, piece 1).
/// Waits for this node's own identity before its first tick, the same reason
/// <c>InviteSweepLoop</c> and <c>PromptAddendaSweepLoop</c> do: the sweep is scoped to this
/// node's own registrations and has nothing to scope to until bootstrap has run.
/// </summary>
public sealed class OrchestratorPresenceSweepLoop(
    OrchestratorPresenceSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options,
    ILogger<OrchestratorPresenceSweepLoop> logger)
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
                await engine.SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Orchestrator presence sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.OrchestratorPresenceSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
