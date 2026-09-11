using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
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
                // Re-read before every resume rather than trusting the one read above for the
                // whole loop (independent pre-PR review, cycle 4, adversarial lens: the same
                // stale-read shape as the force-clear below): a resume can take a while, and a
                // run this loop just resumed can fail the zero-work way and raise a fresh hold
                // before the next one starts. Every run still held then belongs to that hold's
                // own probe and backoff, not to this loop.
                if (await engine.CurrentHoldAsync(nodeId, cancellationToken) is { LaunchHoldActive: true })
                {
                    return;
                }

                await supervisor.ResumeLaunchHeldRunAsync(run, cancellationToken);
            }

            return;
        }

        if (await ClearIfLastProbeIsStillRunningAsync(nodeId, hold, cancellationToken))
        {
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
            // The doc says active but nothing is left held — every held run's own claim moved on
            // since (independent pre-PR review, cycle 3, both lenses), the shape a run reaches
            // when h9k task abandon or a lease-expiry requeue-and-reclaim retires it with
            // RunSuperseded while it sat LaunchHeld, taking it out of every held-run query without
            // ever completing a session for ClearIfEvidencedAsync to read. Left alone, this hold
            // would never clear again — no held run remains for the next sweep to find, either —
            // so the dispatcher's claim gate would stay shut for good. Force-clear it instead; a
            // genuinely broken node raises a fresh hold the moment its very next launch fails the
            // same way. This read is only the trigger, never the proof: the engine re-checks under
            // a pinned stream version before it clears, so a run that joins in between keeps the
            // hold standing (independent pre-PR review, cycle 4, adversarial lens).
            if (await engine.ClearIfNothingLeftHeldAsync(nodeId, cancellationToken))
            {
                logger.LogInformation(
                    "Launch hold: cleared with nothing left held — every held run's own claim moved on before a relaunch ever ran");
            }

            return;
        }

        await engine.RecordProbeAsync(nodeId, oldest.Id, cancellationToken);
        logger.LogInformation("Launch hold: probing the oldest held run {RunId}", oldest.Id);
        await supervisor.ResumeLaunchHeldRunAsync(oldest, cancellationToken);
    }

    /// <summary>
    /// How much longer than <see cref="DaemonOptions.LaunchFailureMaxDuration"/> this sweep waits
    /// before trusting "still <see cref="RunState.Running"/>" as proof of a genuine relaunch,
    /// rather than a launch failure whose own detection simply has not landed yet (independent
    /// pre-PR review, cycle 3, both lenses): the elapsed time this check measures is wall-clock
    /// since the probe was recorded, not the resumed session's own reported duration, and the two
    /// can diverge by as much as <see cref="SessionResultWaiter.PostResultGrace"/> (the root
    /// process lingering after writing its terminal result) plus one tail poll — both paid before
    /// <c>RunSupervisor</c> ever records the zero-work result that would otherwise flip this run
    /// out of <see cref="RunState.Running"/>. Without this margin, a probe that genuinely failed
    /// zero-work-shape in under <see cref="DaemonOptions.LaunchFailureMaxDuration"/> of its own
    /// time can still read <c>Running</c> long enough to be mistaken for a working relaunch,
    /// clearing the hold and resuming every other held run into the same still-broken node.
    /// </summary>
    private static readonly TimeSpan RecordingLatencyGrace = SessionResultWaiter.PostResultGrace + TimeSpan.FromSeconds(1);

    /// <summary>
    /// A relaunch that spawned fine and has stayed alive well past the zero-work window is
    /// already real evidence the node can launch, even though it has not finished — without this,
    /// a probe that resumes a long build session keeps the whole node blocked from new claims,
    /// and the NEEDS YOU banner up, for that session's entire length, which can run for an hour or
    /// more (independent pre-PR review, cycle 1, both lenses, low). <see cref="NodeDetails.LaunchHoldLastProbedRunId"/>
    /// is set by the very probe this sweep just recorded (or an earlier tick's), so a run that
    /// left <see cref="RunState.LaunchHeld"/> for <see cref="RunState.Running"/> and is still
    /// there once <see cref="DaemonOptions.LaunchFailureMaxDuration"/> plus
    /// <see cref="RecordingLatencyGrace"/> has passed since that probe counts — the same duration
    /// the zero-work shape itself is measured against, so a session that has outlived it is
    /// definitionally not that shape. A run this probe's resume never actually reached (still
    /// <see cref="RunState.LaunchHeld"/>, or retired away as stale) is left alone; only a
    /// genuinely running resume clears anything here. Returns whether it cleared the hold, so the
    /// caller skips the ordinary backoff-gated probe on the same tick.
    /// </summary>
    private async Task<bool> ClearIfLastProbeIsStillRunningAsync(
        Guid nodeId, NodeDetails hold, CancellationToken cancellationToken)
    {
        if (hold.LaunchHoldLastProbedRunId is not { } probedRunId
            || DateTimeOffset.UtcNow - hold.LaunchHoldLastEventAt
                <= options.Value.LaunchFailureMaxDuration + RecordingLatencyGrace)
        {
            return false;
        }

        RunDetails? probed = await engine.LoadRunAsync(probedRunId, cancellationToken);
        if (probed is not { State: var state } || state != RunState.Running)
        {
            return false;
        }

        return await engine.ClearIfEvidencedAsync(
            nodeId, probed.ProcessStartedAt ?? hold.LaunchHoldLastEventAt, cancellationToken);
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
