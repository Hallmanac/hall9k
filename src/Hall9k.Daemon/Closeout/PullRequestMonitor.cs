using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// The closeout heartbeat: sweeps this node's awaiting-review pull requests through the
/// CloseoutEngine on a gentle interval (minutes, not seconds — reviews and CI move on
/// human timescales, and there is no doorbell from GitHub in a local-first design).
/// The first sweep waits one full interval, which also gives the dispatch loop time to
/// finish node bootstrap.
/// </summary>
public sealed class PullRequestMonitor(
    CloseoutEngine engine,
    IOptions<DaemonOptions> options,
    ILogger<PullRequestMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.PullRequestPollInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            try
            {
                CloseoutSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                if (sweep.RunsInspected > 0)
                {
                    logger.LogDebug(
                        "Closeout sweep inspected {Count} pull request(s), observed {Merges} merge(s)",
                        sweep.RunsInspected, sweep.MergesObserved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Closeout sweep failed; will retry next tick");
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
