using Hall9k.Domain.Shared.ValueObjects;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// The closeout heartbeat: sweeps this node's awaiting-review pull requests through the
/// CloseoutEngine on a gentle interval (minutes, not seconds — reviews and CI move on
/// human timescales, and there is no doorbell from GitHub in a local-first design).
/// The first sweep waits one full interval, which also gives the dispatch loop time to
/// finish node bootstrap.
/// <para>
/// The interval widens on its own when <c>gh</c> is the one in trouble (independent pre-PR
/// review, cycle 3, an explicit acceptance criterion the first cut shipped without): a sweep
/// that reports an inspection failure doubles the wait before the next one, bounded by
/// <see cref="DaemonOptions.PullRequestPollBackoffMaxInterval"/>, and a clean sweep resets it
/// to <see cref="DaemonOptions.PullRequestPollInterval"/> immediately. A rate limit or an
/// outage would otherwise spend a call every base interval forever, for every awaiting-review
/// pull request this node watches, without ever backing off.
/// </para>
/// </summary>
public sealed class PullRequestMonitor(
    CloseoutEngine engine,
    IOptions<DaemonOptions> options,
    ILogger<PullRequestMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefuseUnreadableReviewRerequestDefault();
        TimeSpan baseInterval = options.Value.PullRequestPollInterval;
        TimeSpan currentInterval = baseInterval;
        using PeriodicTimer timer = new(currentInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            bool sweepFailed;
            try
            {
                CloseoutSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                sweepFailed = sweep.Failures > 0;
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
                sweepFailed = true;
                logger.LogWarning(exception, "Closeout sweep failed; will retry next tick");
            }

            currentInterval = ApplyBackoff(
                currentInterval, baseInterval, options.Value.PullRequestPollBackoffMaxInterval, sweepFailed);
            if (timer.Period != currentInterval)
            {
                timer.Period = currentInterval;
                if (sweepFailed)
                {
                    logger.LogWarning(
                        "Closeout sweep hit a gh failure; widening the poll interval to {Interval}",
                        currentInterval);
                }
                else
                {
                    logger.LogInformation(
                        "Closeout sweep succeeded; poll interval reset to {Interval}", currentInterval);
                }
            }
        }
    }

    /// <summary>
    /// Bounded exponential backoff, reset on success (independent pre-PR review, cycle 3): a
    /// failing sweep doubles the current wait, capped at <paramref name="maxInterval"/>, and any
    /// clean sweep drops straight back to <paramref name="baseInterval"/> rather than decaying
    /// gradually — the moment gh answers again, there is no more trouble left to be cautious
    /// about.
    /// </summary>
    internal static TimeSpan ApplyBackoff(
        TimeSpan currentInterval, TimeSpan baseInterval, TimeSpan maxInterval, bool sweepFailed)
    {
        if (!sweepFailed)
        {
            return baseInterval;
        }

        TimeSpan widened = currentInterval + currentInterval;
        return widened > maxInterval ? maxInterval : widened;
    }

    /// <summary>
    /// Says out loud that a misspelled node default was refused. The policy vocabulary maps
    /// anything it does not recognize to Unknown, and Unknown resolves to Disabled, so a
    /// configured "enabeld" would look exactly like deliberately turning the countersign off
    /// and nothing would ever be re-requested. The CLI rejects the same typo loudly on exactly
    /// that rationale (ReviewRerequestOption); a config file has nobody standing at a prompt
    /// to be told, so the refusal is logged once here, where the setting is actually consumed.
    /// A blank value is not a typo — it is the honest "this level has no opinion".
    /// </summary>
    private void RefuseUnreadableReviewRerequestDefault()
    {
        string configured = options.Value.DefaultReviewRerequest;
        if (configured.IsBlank() || ReviewRerequestPolicy.FromInput(configured) != ReviewRerequestPolicy.Unknown)
        {
            return;
        }

        logger.LogWarning(
            "DefaultReviewRerequest is '{Value}', which is not a policy this node recognizes (expected "
            + "enabled/on or disabled/off). The value is refused, so closeout runs as though the "
            + "countersign were off and no pull request will ever be re-requested — fix the setting if "
            + "that is not what you meant (Decisions Log #62).",
            configured);
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
