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
/// where every attempted inspection failed doubles the wait before the next one, bounded by
/// <see cref="DaemonOptions.PullRequestPollBackoffMaxInterval"/>, and a sweep where at least one
/// inspection succeeded resets it to <see cref="DaemonOptions.PullRequestPollInterval"/>
/// immediately. A rate limit or an outage would otherwise spend a call every base interval
/// forever, for every awaiting-review pull request this node watches, without ever backing off.
/// Keying this on "every attempt failed" rather than "any attempt failed" matters (independent
/// pre-PR review, cycle 4): one permanently broken pull request — a malformed URL, a renamed
/// repository — must not pin every OTHER healthy pull request this node watches to the backoff
/// ceiling forever. A skipped run does not count toward "every attempt failed" either
/// (independent pre-PR review, cycle 5): a run the engine passed over without ever calling
/// <c>gh</c> says nothing about whether <c>gh</c> is in trouble, so it is excluded from both
/// sides of the check — it neither corroborates a genuinely broken pull request sitting
/// alongside it, nor, since a permanently-skipped run (a Done task reopened and then
/// unassigned) can sit in the watch set forever, does it get to veto a failure verdict a real
/// <c>gh</c> outage earns (independent pre-PR review, cycle 1).
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
        TimeSpan baseInterval = ClampPollInterval(
            options.Value.PullRequestPollInterval, nameof(DaemonOptions.PullRequestPollInterval),
            new DaemonOptions().PullRequestPollInterval, logger);
        TimeSpan currentInterval = baseInterval;
        using PeriodicTimer timer = new(currentInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            bool sweepFailed;
            try
            {
                CloseoutSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                sweepFailed = IsSweepFailure(sweep);
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
    /// The widest interval <see cref="PeriodicTimer.Period"/> accepts (its setter rejects
    /// anything over <c>uint.MaxValue - 1</c> milliseconds, roughly 49.7 days) — the ceiling
    /// <see cref="ApplyBackoff"/> clamps a configured
    /// <see cref="DaemonOptions.PullRequestPollBackoffMaxInterval"/> to, so a value the daemon
    /// cannot actually hand the timer never reaches it.
    /// </summary>
    internal static readonly TimeSpan MaxSupportedInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Whether a sweep counts as a <c>gh</c> failure for backoff purposes: every run this sweep
    /// looked at threw, and at least one was looked at (independent pre-PR review, cycle 4). A
    /// sweep where some runs succeeded and one keeps failing — a malformed pull request URL, a
    /// renamed repository — is not gh trouble in general, so it must not pin the interval at the
    /// ceiling and delay every other healthy pull request's merge observation. Neither is a
    /// sweep where the rest were merely skipped rather than inspected (independent pre-PR
    /// review, cycle 5): a Done task reopened and then unassigned sits in the watch set
    /// returning <see cref="CloseoutSweepResult.Skipped"/> forever without ever calling
    /// <c>gh</c>, so a lone genuinely-broken pull request alongside one or more of those must not
    /// read as "every attempted inspection failed" on a skip that was never gh's fault — but a
    /// skip must also never veto a failure verdict the failed inspections themselves earn
    /// (independent pre-PR review, cycle 1): that permanently-skipped run sitting in the watch
    /// set forever must not be able to mask a real, ongoing gh outage on every other watched
    /// pull request for as long as it persists. Excluding
    /// <see cref="CloseoutSweepResult.Skipped"/> from the check entirely, rather than requiring
    /// it to be zero, gives both: it neither corroborates nor vetoes. An empty sweep (nothing
    /// watched) is not a failure either; there was nothing to fail at.
    /// </summary>
    internal static bool IsSweepFailure(CloseoutSweepResult sweep) =>
        sweep.Failures > 0 && sweep.RunsInspected == 0;

    /// <summary>
    /// Bounded exponential backoff, reset on success (independent pre-PR review, cycle 3): a
    /// failing sweep doubles the current wait, capped at <paramref name="maxInterval"/>, and any
    /// clean sweep drops straight back to <paramref name="baseInterval"/> rather than decaying
    /// gradually — the moment gh answers again, there is no more trouble left to be cautious
    /// about. The cap is never allowed below <paramref name="baseInterval"/> (independent pre-PR
    /// review, cycle 4): a misconfigured ceiling at or under the base would otherwise invert the
    /// backoff into polling a failing gh more often than a healthy one, and a ceiling of zero
    /// would hand <c>PeriodicTimer.Period</c> a value it rejects, outside the loop's own
    /// try/catch. The cap is equally never allowed above <see cref="MaxSupportedInterval"/>
    /// (independent pre-PR review, cycle 1's adversarial lens): a misconfigured ceiling above
    /// what <c>PeriodicTimer.Period</c> accepts — an operator setting
    /// <c>PullRequestPollBackoffMaxInterval=60</c> meaning minutes, but landing as 60 days once
    /// bound as a bare-integer <see cref="TimeSpan"/> — would otherwise reach that same setter
    /// once enough doubling caught up to it, outside the loop's own try/catch, and stop the
    /// closeout monitor for the life of the daemon.
    /// </summary>
    internal static TimeSpan ApplyBackoff(
        TimeSpan currentInterval, TimeSpan baseInterval, TimeSpan maxInterval, bool sweepFailed)
    {
        if (!sweepFailed)
        {
            return baseInterval;
        }

        TimeSpan effectiveMax = maxInterval < baseInterval ? baseInterval : maxInterval;
        if (effectiveMax > MaxSupportedInterval)
        {
            effectiveMax = MaxSupportedInterval;
        }

        TimeSpan widened = currentInterval + currentInterval;
        return widened > effectiveMax ? effectiveMax : widened;
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

    /// <summary>
    /// Refuses a configured base interval <see cref="PeriodicTimer"/>'s constructor would throw
    /// on, outside this loop's own try/catch — a misconfigured
    /// <see cref="DaemonOptions.PullRequestPollInterval"/> would otherwise crash the monitor
    /// (and, unguarded here, the daemon) before a single sweep ever ran. Zero, negative, or
    /// below the one-millisecond floor <see cref="PeriodicTimer"/>'s constructor itself requires
    /// falls back to <see cref="DaemonOptions.PullRequestPollInterval"/>'s shipped default rather
    /// than refusing to start: the same "refuse loudly, keep the node up" posture as
    /// <see cref="RefuseUnreadableReviewRerequestDefault"/>. A positive sub-millisecond value
    /// (independent pre-PR review, cycle 2's adversarial lens) truncates to zero milliseconds and
    /// would otherwise reach the constructor unclamped exactly like zero or negative does, since
    /// this method's own lower-bound check previously excluded it. Above
    /// <see cref="MaxSupportedInterval"/> clamps down to it instead (independent pre-PR review,
    /// cycle 2): the same bare-integer misconfiguration <see cref="ApplyBackoff"/> already clamps
    /// its own ceiling against (<c>PullRequestPollInterval=60</c> meaning minutes, landing as 60
    /// days once bound) would otherwise reach the constructor unclamped, since this method
    /// previously guarded only the lower half of the range the constructor accepts.
    /// <para>
    /// <paramref name="settingName"/> and <paramref name="fallback"/> are the caller's own —
    /// never a name or a default hardcoded here — so a monitor other than closeout's own can
    /// reuse this pure clamp without misnaming the setting it is actually guarding
    /// (<see cref="Hall9k.Daemon.AutoPrReview.AutoPrReviewMonitor"/>'s own
    /// <c>AutoPrReviewPollInterval</c>, independent pre-PR review cycle 1, both lenses: a
    /// misconfigured <c>AutoPrReviewPollInterval</c> used to log against
    /// <c>PullRequestPollInterval</c> and silently fall back to that setting's own default
    /// instead of its own).
    /// </para>
    /// </summary>
    internal static TimeSpan ClampPollInterval(TimeSpan configured, string settingName, TimeSpan fallback, ILogger logger)
    {
        if (configured < TimeSpan.FromMilliseconds(1))
        {
            logger.LogWarning(
                "{SettingName} is {Configured}, which is not a positive interval; falling "
                + "back to {Fallback} so the monitor can still start.",
                settingName, configured, fallback);
            return fallback;
        }

        if (configured > MaxSupportedInterval)
        {
            logger.LogWarning(
                "{SettingName} is {Configured}, which is above what PeriodicTimer accepts; "
                + "clamping to {Clamped} so the monitor can still start.",
                settingName, configured, MaxSupportedInterval);
            return MaxSupportedInterval;
        }

        return configured;
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
