using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The on-demand half of the CLI-owned lifecycle (Decisions Log #31): launch h9kd
/// detached from this terminal, refuse politely when one already runs, and surface the
/// startup catch-up report so the cost of being down is visible as latency.
/// </summary>
public sealed class DaemonStartCommand : Hall9kAsyncCommand<DaemonStartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--binary <PATH>")]
        [Description("Explicit h9kd binary to launch, starting it directly rather than through a registered autostart job; a relative path is resolved against the current directory and must exist (default: the installed one in ~/.hall9k/bin, then the build output)")]
        public string? Binary { get; init; }
    }

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        DaemonLifecycle.StartAsync(DaemonAutostart.ForCurrentPlatform(), settings.Binary, cancellationToken);
}
