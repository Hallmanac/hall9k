using Hall9k.Connectors.Releases;

namespace Hall9k.Daemon.Releases;

/// <summary>
/// The heartbeat behind <see cref="ReleaseNoticeEngine"/> (idea 93c1d24d, card 2). Waits for this
/// node's own identity (<paramref name="awaitNodeId"/>, which production wires to
/// <see cref="NodeContext.WaitForInitializationAsync"/>, so the loop is testable without a
/// database), then about a minute more (so a daemon that has just started is not also
/// first in line at GitHub), checks once, and then checks once per interval. The interval is read
/// fresh from the config file after every check, so <c>h9k config set --release-check-interval-hours</c>
/// governs the wait that follows with no restart. A <see cref="TimeProvider"/> drives every wait, so
/// a test steps the clock instead of sleeping.
/// </summary>
public sealed class ReleaseNoticeLoop(
    ReleaseNoticeEngine engine, Func<CancellationToken, Task<Guid>> awaitNodeId, ReleaseCheckIntervalReader readInterval,
    TimeProvider clock, ILogger<ReleaseNoticeLoop> logger)
    : BackgroundService
{
    /// <summary>How long after the node's identity is ready the first check runs.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest single timer the loop asks for: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// refuses anything past about 49 days, so a longer interval is waited out in slices of this size
    /// rather than faulting the loop (and, with it, the daemon host).
    /// </summary>
    public static readonly TimeSpan LongestSingleWait = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid nodeId;
        try
        {
            nodeId = await awaitNodeId(stoppingToken);
            await Task.Delay(FirstCheckDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.CheckOnceAsync(nodeId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Release check failed unexpectedly; will check again next interval");
            }

            try
            {
                await WaitAsync(await readInterval(stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        for (TimeSpan remaining = interval; remaining > TimeSpan.Zero; remaining -= LongestSingleWait)
        {
            await Task.Delay(remaining < LongestSingleWait ? remaining : LongestSingleWait, clock, cancellationToken);
        }
    }
}
