using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// The heartbeat behind <c>h9k plan start</c>: waits on the doorbell, sweeps on a timer as the safety net, and steps the
/// plans started on this node.
/// <para>
/// Its own hosted service, and not a step in the dispatch loop or the challenge loop, so a plan's long refinement session
/// holds up neither task dispatch, a challenge, nor another plan's next step. The first sweep runs immediately, so a plan
/// started while the daemon was down is picked up the moment it comes back, and so is the settling of a session the last
/// daemon was stopped in the middle of. A session that ends rings the loop's own doorbell, so the plan's next step does not
/// wait for the timer.
/// </para>
/// </summary>
public sealed class PlanLoop(
    PlanEngine engine,
    NodeContext node,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<PlanLoop> logger) : BackgroundService
{
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before anything else, and before the listener: every dispatch below names this node, and the
        // dispatch loop is what resolves that identity. Waiting here also means Postgres is up.
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        engine.OnWork = () => _doorbell.Release();
        Task listener = ListenAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PlanSweepResult sweep = await engine.PollOnceAsync(stoppingToken);

                // Every part of the tally that is not zero gets said, and each says only what it is.
                if (sweep.Steps > 0 || sweep.Recovered > 0 || sweep.Expired > 0)
                {
                    logger.LogInformation(
                        "Plans: {Steps} step(s) taken, {Dispatched} refinement session(s) dispatched, {Recovered} left "
                        + "behind by a stopped daemon were recorded as failed, {Expired} dispatch(es) of a node that never "
                        + "came back were ended on the ceiling",
                        sweep.Steps, sweep.Dispatched, sweep.Recovered, sweep.Expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plan sweep failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(options.Value.PlanPollInterval, stoppingToken);
                while (_doorbell.CurrentCount > 0)
                {
                    await _doorbell.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await listener;
        await engine.StopAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Its own listener on the shared channel. NOTIFY carries no payload by design (log #8), so
    /// every doorbell wakes every listener and each one decides for itself whether it has work,
    /// which here costs a query against the plan projection and one against the task projection.
    /// </summary>
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection listener = new(connection.ConnectionString);
                await listener.OpenAsync(cancellationToken);
                listener.Notification += (_, _) => _doorbell.Release();

                await using (NpgsqlCommand listen = new($"LISTEN {Doorbell.Channel}", listener))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    await listener.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plan doorbell listener dropped; reconnecting in 5s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
