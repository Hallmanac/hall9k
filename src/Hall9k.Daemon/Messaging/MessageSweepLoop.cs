using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// The message sweep's own heartbeat (idea 202383dc, M1b; Brian's ruling 2026-09-13): 15 to 25
/// seconds with jitter while this node has something unflushed or unread, or held work; 30 to 45
/// seconds with jitter when idle — dropping to the fast range the moment the next sweep finds
/// something, and skipping the wait entirely the tick right after this node's own push, so a burst
/// of sends does not sit behind a full idle interval waiting to be noticed. No doorbell: unlike
/// most of this daemon's other loops, nothing here needs one — <see cref="MessageSweepEngine"/>
/// re-evaluates "is there anything to do" fresh at the top of every tick, so a CLI send queued
/// between ticks is found on the very next one regardless of what woke this loop.
/// </summary>
public sealed class MessageSweepLoop(
    MessageSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options, ILogger<MessageSweepLoop> logger)
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
            TimeSpan delay;
            try
            {
                MessageSweepResult sweep = await engine.SweepOnceAsync(stoppingToken);
                delay = sweep.JustPushed ? TimeSpan.Zero : JitteredInterval(sweep.ActiveCadence, options.Value);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Message sweep failed; will retry next tick");
                delay = JitteredInterval(activeCadence: false, options.Value);
            }

            if (delay <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Picks a jittered interval from the configured active or idle range, falling back to that
    /// range's own shipped default when the configured min/max is nonsensical (min below one
    /// second, or above max) — the same "refuse loudly by falling back, keep the node up" posture
    /// <c>PullRequestMonitor.ClampPollInterval</c> already gives a misconfigured poll interval,
    /// since <c>Random.Next</c> itself throws on an inverted range rather than merely misbehaving.
    /// </summary>
    internal static TimeSpan JitteredInterval(bool activeCadence, DaemonOptions options)
    {
        DaemonOptions defaults = new();
        (int min, int max) = activeCadence
            ? (options.MessageActivePollMinSeconds, options.MessageActivePollMaxSeconds)
            : (options.MessageIdlePollMinSeconds, options.MessageIdlePollMaxSeconds);

        if (min < 1 || max < min)
        {
            (min, max) = activeCadence
                ? (defaults.MessageActivePollMinSeconds, defaults.MessageActivePollMaxSeconds)
                : (defaults.MessageIdlePollMinSeconds, defaults.MessageIdlePollMaxSeconds);
        }

        int seconds = Random.Shared.Next(min, max + 1);
        return TimeSpan.FromSeconds(seconds);
    }
}
