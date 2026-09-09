using Hall9k.Daemon.Closeout;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// The auto-pr-review heartbeat, the closeout monitor's own interval-with-backoff shape
/// (<see cref="PullRequestMonitor"/>) applied to a second, independent poll: a gentle interval —
/// a reviewer assignment is a human-timescale event exactly like a review or a CI result — that
/// widens on its own when <c>gh</c> is the one in trouble and resets the moment it answers again.
/// The backoff mechanics themselves are not reimplemented here: <see cref="PullRequestMonitor.ApplyBackoff"/>
/// and <see cref="PullRequestMonitor.ClampPollInterval"/> are pure, generic, and already proven
/// against their own edge cases, so this monitor calls them directly on its own timer and options
/// rather than carrying a second copy that could drift from the first.
/// </summary>
public sealed class AutoPrReviewMonitor(
    AutoPrReviewEngine engine,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<AutoPrReviewMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before the announcement below, and so before anything else this loop does: that
        // announcement is the one thing here that runs ahead of its own first timer tick, and
        // both halves of it need this node's identity — the cutoff it records is keyed on
        // NodeId, and NodeContext.NodeId throws until the dispatch loop has waited for Postgres
        // and bootstrapped. The sibling services that sweep immediately (CardPublicationLoop,
        // JiraWriteRetryLoop) wait on exactly this, for exactly this reason; the ones that tick
        // before sweeping get it for free. Origin incident (2026-08-21, restated against this
        // very loop by this branch's own pre-PR review, cycle 1, adversarial lens): the host
        // starts every remaining hosted service the moment DispatchLoop reaches its first await,
        // so an unguarded read here threw "NodeContext not initialized yet" on every real daemon
        // start — the announcement deterministically never printed, and the no-backfill cutoff
        // was first recorded a full poll interval later instead, holding every request that
        // arrived in that window as if it predated this install's own adoption.
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Visibility before the first tick (Decisions Log #161): one Info line per project
        // naming whether auto pr-review is on or off there, printed at the default too. This is
        // also where this install's own no-backfill cutoff is first recorded, which is why it
        // runs even when nothing is registered yet. A failure here — an unreachable database
        // moments after start — must not take the poll loop down with it: the sweep's own first
        // tick records the cutoff and the loop keeps its ordinary backoff.
        try
        {
            await engine.AnnounceSettingsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Auto-pr-review could not announce its per-project settings at start; polling anyway");
        }

        TimeSpan baseInterval = PullRequestMonitor.ClampPollInterval(
            options.Value.AutoPrReviewPollInterval, nameof(DaemonOptions.AutoPrReviewPollInterval),
            new DaemonOptions().AutoPrReviewPollInterval, logger);
        TimeSpan currentInterval = baseInterval;
        using PeriodicTimer timer = new(currentInterval);
        while (await NextTickAsync(timer, stoppingToken))
        {
            bool sweepFailed;
            try
            {
                AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(stoppingToken);
                sweepFailed = IsSweepFailure(sweep);
                if (sweep.ProjectsInspected > 0)
                {
                    logger.LogDebug(
                        "Auto-pr-review sweep inspected {Count} project(s), created {Created} "
                        + "task(s), recalled {Recalled} assignment(s)",
                        sweep.ProjectsInspected, sweep.TasksCreated, sweep.AssignmentsRecalled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                sweepFailed = true;
                logger.LogWarning(exception, "Auto-pr-review sweep failed; will retry next tick");
            }

            currentInterval = PullRequestMonitor.ApplyBackoff(
                currentInterval, baseInterval, options.Value.AutoPrReviewPollBackoffMaxInterval, sweepFailed);
            if (timer.Period != currentInterval)
            {
                timer.Period = currentInterval;
                logger.LogInformation(
                    sweepFailed
                        ? "Auto-pr-review sweep hit a gh failure; widening the poll interval to {Interval}"
                        : "Auto-pr-review sweep succeeded; poll interval reset to {Interval}",
                    currentInterval);
            }
        }
    }

    /// <summary>
    /// The same "every attempted inspection failed" rule <see cref="PullRequestMonitor.IsSweepFailure"/>
    /// states for closeout, restated for this sweep's own result shape: a lone broken project must
    /// not pin every other project's healthy reads to the backoff ceiling, and an empty sweep
    /// (no projects registered at all) is not a failure either.
    /// </summary>
    internal static bool IsSweepFailure(AutoPrReviewSweepResult sweep) =>
        sweep.ProjectsFailed > 0 && sweep.ProjectsInspected == sweep.ProjectsFailed;

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
