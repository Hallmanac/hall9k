using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Probes hourly for runs parked on token-budget exhaustion (backlog 40) and retries
/// each. The subscription window resets on a known-ish clock rather than an event the
/// platform can watch for, so a patient poll is the whole recovery mechanism — there is no
/// doorbell to ring here, only a clock to wait on.
/// </summary>
public sealed class TokenBudgetRetryMonitor(
    TokenBudgetRetryEngine engine,
    IOptions<DaemonOptions> options,
    ILogger<TokenBudgetRetryMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.TokenBudgetRetryInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            try
            {
                int retried = await engine.RetryParkedRunsAsync(stoppingToken);
                if (retried > 0)
                {
                    logger.LogInformation("Token-budget retry sweep resumed {Count} run(s)", retried);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Token-budget retry sweep failed; will retry next tick");
            }
        }
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
