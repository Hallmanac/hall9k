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
    IOptions<DaemonOptions> options,
    ILogger<AutoPrReviewMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                        "Auto-pr-review sweep inspected {Count} opted-in project(s), created {Created} "
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
    /// not pin every other opted-in project's healthy reads to the backoff ceiling, and an empty
    /// sweep (nothing opted in) is not a failure either.
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
