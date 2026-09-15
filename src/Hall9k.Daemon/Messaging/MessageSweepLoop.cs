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
                delay = sweep.JustPushed ? TimeSpan.Zero : JitteredInterval(sweep.ActiveCadence, options.Value, logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Message sweep failed; will retry next tick");
                delay = JitteredInterval(activeCadence: false, options.Value, logger);
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
    /// The widest interval this loop's own <see cref="Task.Delay(TimeSpan,CancellationToken)"/>
    /// call accepts — <c>int.MaxValue</c> milliseconds, about 24.8 days; a delay any longer than
    /// this throws <see cref="ArgumentOutOfRangeException"/> outside this loop's own try/catch and
    /// stops the message sweep for the life of the daemon. Deliberately its own constant rather
    /// than reusing <c>PullRequestMonitor.MaxSupportedInterval</c> (<c>uint.MaxValue - 1</c>
    /// milliseconds, about 49.7 days): that ceiling belongs to <c>PeriodicTimer.Period</c>, which
    /// this loop does not use, and is itself too wide for what <see cref="Task.Delay(TimeSpan,CancellationToken)"/>
    /// actually accepts (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    internal static readonly TimeSpan MaxSupportedInterval = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Picks a jittered interval from the configured active or idle range, falling back to that
    /// range's own shipped default when the configured min/max is nonsensical (min below one
    /// second, or above max) — the same "refuse loudly by falling back, keep the node up" posture
    /// <c>PullRequestMonitor.ClampPollInterval</c> already gives a misconfigured poll interval,
    /// since <c>Random.Next</c> itself throws on an inverted range rather than merely misbehaving.
    /// Also clamps whichever bound survives that fallback down to <see cref="MaxSupportedInterval"/>
    /// (in whole seconds): unlike the four review-cycle caps and the concurrency settings,
    /// <c>h9k config set --message-poll-*</c> accepts any positive int with no upper bound, so a
    /// value like 5,000,000 seconds (about 57.9 days) passes that command's own validation cleanly
    /// and would otherwise reach <see cref="Task.Delay(TimeSpan,CancellationToken)"/> unclamped —
    /// either overflowing <c>Random.Shared.Next(min, max + 1)</c> when <c>max</c> is near
    /// <c>int.MaxValue</c>, or handing the delay itself a <see cref="TimeSpan"/> the timer rejects
    /// outright, either way crashing the daemon under the default
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c> on every restart (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    internal static TimeSpan JitteredInterval(bool activeCadence, DaemonOptions options, ILogger logger)
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

        int maxSupportedSeconds = (int)MaxSupportedInterval.TotalSeconds;
        if (max > maxSupportedSeconds)
        {
            logger.LogWarning(
                "The {Cadence} message-poll-max is {Max}s, which Task.Delay cannot wait for directly "
                + "(the widest interval it accepts is about {MaxSupportedDays:F1} days); clamping to "
                + "{Clamped}s so the message sweep keeps running.",
                activeCadence ? "active" : "idle", max, MaxSupportedInterval.TotalDays, maxSupportedSeconds);
            max = maxSupportedSeconds;
        }

        if (min > max)
        {
            min = max;
        }

        int seconds = Random.Shared.Next(min, max + 1);
        return TimeSpan.FromSeconds(seconds);
    }
}
