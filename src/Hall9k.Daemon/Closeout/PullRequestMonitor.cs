using Hall9k.Domain.Shared.ValueObjects;
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
        RefuseUnreadableReviewRerequestDefault();
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
