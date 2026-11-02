using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Feedback;

/// <summary>
/// Runs <see cref="FeedbackStepEngine"/> on a cadence. A step does not need the plan loop's doorbell channel: the hold arrives
/// on the task's stream from a dependency sweep, an observation, or a queue command, and the sweep that looks for it is cheap, so
/// polling is the whole mechanism. A check that ends rings the loop's own doorbell, so the step after it does not wait out the
/// interval.
/// </summary>
public sealed class FeedbackStepLoop(
    FeedbackStepEngine engine,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<FeedbackStepLoop> logger) : BackgroundService
{
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every dispatch names this node, and the dispatch loop is what resolves that identity.
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        engine.OnWork = () => _doorbell.Release();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                FeedbackStepSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                if (sweep.Steps > 0)
                {
                    logger.LogInformation(
                        "Feedback steps: {Steps} step(s) taken, {Dispatched} check session(s) dispatched",
                        sweep.Steps, sweep.Dispatched);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Feedback step sweep failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(options.Value.FeedbackStepPollInterval, stoppingToken);
                while (_doorbell.CurrentCount > 0)
                {
                    await _doorbell.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await engine.StopAsync(TimeSpan.FromSeconds(10));
    }
}
