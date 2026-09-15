using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Invites;

/// <summary>
/// The heartbeat behind <see cref="InviteSweepEngine"/> (idea 202383dc, T2). Waits for this node's
/// own identity before its first tick — the same reason <c>MessageSweepLoop</c> does — since the
/// engine reads <c>NodeContext.NodeId</c>/<c>OwnerId</c> on every sweep.
/// </summary>
public sealed class InviteSweepLoop(
    InviteSweepEngine engine, NodeContext node, IOptions<DaemonOptions> options, ILogger<InviteSweepLoop> logger)
    : BackgroundService
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
                InviteSweepResult sweep = await engine.SweepOnceAsync(stoppingToken);
                if (sweep.InvitesSpent > 0)
                {
                    logger.LogInformation("Invite sweep: {InvitesSpent} invite(s) matched and vouched in", sweep.InvitesSpent);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Invite sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(options.Value.InviteSweepPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
