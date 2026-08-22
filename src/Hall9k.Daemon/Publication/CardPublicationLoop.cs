using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hall9k.Daemon.Publication;

/// <summary>
/// The heartbeat behind h9k task push-to-jira: waits on the doorbell, sweeps on a timer as the
/// safety net, and runs whatever publication requests this owner has outstanding.
/// <para>
/// It is its own hosted service rather than a step in the dispatch loop for one reason: it blocks
/// on an agent session, and the dispatch loop is what launches every run on this node. Waiting
/// for somebody's Jira card inside that loop would hold up real work — which is the same lesson
/// the blocker-synthesis ceiling records (Decisions Log #36), applied by keeping the wait out of
/// that loop entirely rather than by bounding it tightly.
/// </para>
/// <para>
/// The first sweep runs immediately rather than after a full interval, unlike the closeout
/// monitor. Closeout waits on other people (a review, CI); this waits on a request a human made
/// deliberately, and a request made while the daemon was down should be picked up the moment it
/// comes back rather than half a minute later. "Immediately" means as soon as this node knows
/// who it is, which is the one thing a sweep cannot do without: the sibling services get node
/// bootstrap for free by ticking before they sweep, and this one waits on it explicitly instead.
/// </para>
/// </summary>
public sealed class CardPublicationLoop(
    CardPublicationEngine engine,
    NodeContext node,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<CardPublicationLoop> logger) : BackgroundService
{
    private readonly SemaphoreSlim _doorbell = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before anything else, and before the listener: every query below is scoped to this
        // node or its owner, and the dispatch loop is what resolves that identity. Waiting here
        // also means Postgres is up, since bootstrap had to reach it.
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
                CardPublicationSweepResult sweep = await engine.PollOnceAsync(stoppingToken);

                // Every part of the tally that is not zero gets said, and each says only what it
                // is. A sweep that adopted a stranded session or refused a request did something
                // to somebody's task, and reporting either as a session that ran — or as nothing
                // at all — is the log telling an operator something that did not happen.
                if (sweep.Dispatched > 0 || sweep.Adopted > 0 || sweep.Refused > 0)
                {
                    logger.LogInformation(
                        "Card publication: {Dispatched} session(s) ran and {Linked} produced a verified card, "
                        + "{Adopted} session(s) adopted from an earlier run, {Refused} request(s) answered without "
                        + "a session",
                        sweep.Dispatched, sweep.Linked, sweep.Adopted, sweep.Refused);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Card publication sweep failed; continuing");
            }

            try
            {
                await _doorbell.WaitAsync(options.Value.CardPublicationPollInterval, stoppingToken);
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
    /// every doorbell wakes every listener and each one decides for itself whether it has work —
    /// which here costs one indexed query against a projection.
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
                logger.LogWarning(exception, "Card publication doorbell listener dropped; reconnecting in 5s");
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
