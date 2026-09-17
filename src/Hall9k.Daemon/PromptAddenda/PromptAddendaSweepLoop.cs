using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.PromptAddenda;

/// <summary>
/// The heartbeat behind <see cref="PromptAddendaSweepEngine"/> (idea b9b09779, piece 6). Waits for
/// this node's own identity before its first tick, the same reason <c>InviteSweepLoop</c> does.
/// </summary>
public sealed class PromptAddendaSweepLoop(
    PromptAddendaSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options,
    ILogger<PromptAddendaSweepLoop> logger)
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
                PromptAddendaSweepResult sweep = await engine.SweepOnceAsync(stoppingToken);
                if (sweep.Pushed > 0 || sweep.Materialized > 0)
                {
                    logger.LogInformation(
                        "Prompt-addenda sweep: {Pushed} pushed to the ledger, {Materialized} materialized locally",
                        sweep.Pushed, sweep.Materialized);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Prompt-addenda sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.PromptAddendaSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
