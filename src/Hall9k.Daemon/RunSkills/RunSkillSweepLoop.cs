using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.RunSkills;

/// <summary>
/// The heartbeat behind <see cref="RunSkillSweepEngine"/> (idea b9b09779, piece 4). Waits for
/// this node's own identity before its first tick, the same reason <c>PromptAddendaSweepLoop</c>
/// does: the push half signs its ledger commits with this node's own key.
/// </summary>
public sealed class RunSkillSweepLoop(
    RunSkillSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options,
    ILogger<RunSkillSweepLoop> logger)
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
                RunSkillSweepResult sweep = await engine.SweepOnceAsync(stoppingToken);
                if (sweep.Discovered > 0 || sweep.Pushed > 0)
                {
                    logger.LogInformation(
                        "Run-skill sweep: {Discovered} discovery request(s) answered, {Pushed} pushed to the ledger",
                        sweep.Discovered, sweep.Pushed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Run-skill sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.RunSkillSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
