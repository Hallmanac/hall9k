using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Graceful shutdown: SIGTERM (or launchctl bootout when autostart owns the job, so
/// crash-restart cannot resurrect it) — in-flight event appends finish, detached agents
/// keep running for adoption on the next start (Decisions Log #31).
/// </summary>
public sealed class DaemonStopCommand : Hall9kAsyncCommand<DaemonStopCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        DaemonLifecycle.StopAsync(DaemonAutostart.ForCurrentPlatform(), cancellationToken);
}
