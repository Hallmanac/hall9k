using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.JiraWrites;

/// <summary>
/// The heartbeat behind an expired twg login: no event on this machine says "the browser login
/// just finished", so this loop's only signal is the clock, polling on
/// <see cref="DaemonOptions.JiraWriteRetryInterval"/> (Brian's design, 2026-08-28). It is its own
/// hosted service rather than folded into the dispatch loop for the same reason
/// <c>CardPublicationLoop</c> is its own: a pending write belongs to no run and no lease, so
/// nothing about dispatching agent sessions has any business gating it.
/// </summary>
public sealed class JiraWriteRetryLoop(
    JiraWriteRetryEngine engine,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<JiraWriteRetryLoop> logger) : BackgroundService
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
                JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                if (sweep.Retried > 0)
                {
                    logger.LogInformation(
                        "Jira write retry: {Retried} pending write(s) re-attempted, {Succeeded} went through",
                        sweep.Retried, sweep.Succeeded);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Jira write retry sweep failed; continuing");
            }

            try
            {
                await Task.Delay(options.Value.JiraWriteRetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
