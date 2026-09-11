using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run.Projections;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Probes a standing node-wide launch hold and resumes what it clears (task: a session that
/// exits at once with no work done is treated as the node failing to launch sessions) — the
/// action half of the feature, ties <see cref="LaunchHoldEngine"/>'s own state (which carries no
/// dependency on <c>RunSupervisor</c>, to avoid the cycle <c>RunSupervisor</c>'s own dependency on
/// <see cref="LaunchHoldEngine"/> would otherwise close) to <see cref="RunSupervisor.ResumeLaunchHeldRunAsync"/>,
/// the resume action itself. Its own hosted service rather than a step in the dispatch loop, the
/// same reasoning <c>CardPublicationLoop</c>'s own doc gives: a probe blocks on spawning a real
/// agent session, and the dispatch loop is what launches every other run on this node.
/// <para>
/// No doorbell: nothing on this machine observes "the credential just got fixed" or "the network
/// just came back", so a short poll (<see cref="DaemonOptions.PollInterval"/>, the same cadence
/// the dispatch loop itself sweeps on) is the whole mechanism, exactly as
/// <c>TokenBudgetRetryMonitor</c>'s own doc states for its very different clock. Cheap on every
/// idle tick — a single doc read — since no hold stands on the overwhelming majority of them.
/// </para>
/// </summary>
public sealed class LaunchHoldMonitor(
    LaunchHoldEngine engine,
    RunSupervisor supervisor,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<LaunchHoldMonitor> logger) : BackgroundService
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
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Launch-hold sweep failed; will check again next tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Either probes the oldest held run, if the hold's own doubling backoff says it is due, or —
    /// when the hold reads inactive — resumes any run still <c>LaunchHeld</c> left behind by a
    /// clear this sweep did not itself observe (another sweep's clear, or one that landed just
    /// before a restart). The second branch is naturally idempotent: resuming a run flips its own
    /// state off <c>LaunchHeld</c> before the next tick can see it again, so a lingering run is
    /// found at most once.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        NodeDetails? hold = await engine.CurrentHoldAsync(nodeId, cancellationToken);
        if (hold is not { LaunchHoldActive: true })
        {
            foreach (RunDetails run in await engine.HeldRunsAsync(nodeId, cancellationToken))
            {
                await supervisor.ResumeLaunchHeldRunAsync(run, cancellationToken);
            }

            return;
        }

        TimeSpan backoff = BackoffForProbe(
            hold.LaunchHoldProbeCount, options.Value.SessionErrorRetryBackoff,
            options.Value.LaunchHoldProbeBackoffMaxInterval);
        if (DateTimeOffset.UtcNow < hold.LaunchHoldLastEventAt + backoff)
        {
            return;
        }

        RunDetails? oldest = await engine.OldestHeldRunAsync(nodeId, cancellationToken);
        if (oldest is null)
        {
            // The doc says active but nothing is left held — a race between a clear and this
            // read, or every held run's claim moved on since. Nothing to probe this tick.
            return;
        }

        await engine.RecordProbeAsync(nodeId, oldest.Id, cancellationToken);
        logger.LogInformation("Launch hold: probing the oldest held run {RunId}", oldest.Id);
        await supervisor.ResumeLaunchHeldRunAsync(oldest, cancellationToken);
    }

    /// <summary>
    /// Doubles from <paramref name="start"/> each probe, capped at <paramref name="cap"/> — the
    /// same widening-with-a-floor shape <c>DaemonOptions.PullRequestPollBackoffMaxInterval</c>'s
    /// own doc describes. Bounded to a handful of iterations regardless of how large
    /// <paramref name="probeCount"/> grows (an outage lasting days still only doubles until the
    /// cap, then stays there), so this never risks overflowing <see cref="TimeSpan"/> arithmetic
    /// on a hold that stands a long time.
    /// </summary>
    internal static TimeSpan BackoffForProbe(int probeCount, TimeSpan start, TimeSpan cap)
    {
        TimeSpan current = start;
        for (int i = 0; i < probeCount; i++)
        {
            if (current >= cap)
            {
                return cap;
            }

            current += current;
        }

        return current > cap ? cap : current;
    }
}
