using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hall9k.Daemon.Challenge;

/// <summary>
/// The heartbeat behind <c>h9k task challenge</c>: waits on the doorbell, sweeps on a timer as the safety net, and runs
/// the challenge requests made on this node.
/// <para>
/// Its own hosted service, and not a step in the card-publication loop or the dispatch loop, because a challenge blocks on
/// an agent session for up to <see cref="DaemonOptions.ChallengeTimeout"/>: waiting for one inside either would hold up
/// every card or every claim behind it. The first sweep runs immediately, so a request made while the daemon was down is
/// picked up the moment it comes back, and so is the settling of a session the last daemon was stopped in the middle of.
/// </para>
/// </summary>
public sealed class DraftChallengeLoop(
    DraftChallengeEngine engine,
    NodeContext node,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<DraftChallengeLoop> logger) : BackgroundService
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

        Task listener = ListenAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DraftChallengeSweepResult sweep = await engine.PollOnceAsync(stoppingToken);

                // Every part of the tally that is not zero gets said, and each says only what it is.
                if (sweep.Dispatched > 0 || sweep.Failed > 0 || sweep.Recovered > 0 || sweep.Expired > 0)
                {
                    logger.LogInformation(
                        "Draft challenge: {Dispatched} session(s) ran and {Verdicts} recorded a verdict, {Failed} "
                        + "ended with a failure, {Recovered} left behind by a stopped daemon were recorded as failed, "
                        + "{Expired} dispatch(es) of a node that never came back were ended on the ceiling",
                        sweep.Dispatched, sweep.Verdicts, sweep.Failed, sweep.Recovered, sweep.Expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Draft challenge sweep failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(options.Value.ChallengePollInterval, stoppingToken);
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
    }

    /// <summary>
    /// Its own listener on the shared channel. NOTIFY carries no payload by design (log #8), so
    /// every doorbell wakes every listener and each one decides for itself whether it has work,
    /// which here costs two queries against the task projection (the sweep's recovery query and its pending query).
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
                logger.LogWarning(exception, "Draft challenge doorbell listener dropped; reconnecting in 5s");
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
