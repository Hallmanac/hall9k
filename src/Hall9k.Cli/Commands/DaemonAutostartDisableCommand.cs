using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Fully unregister start-at-login; a launchd-owned daemon is stopped in the process (and says so).</summary>
public sealed class DaemonAutostartDisableCommand : Hall9kAsyncCommand<DaemonAutostartDisableCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
        if (!autostart.IsSupported)
        {
            await Console.Error.WriteLineAsync(autostart.NotSupportedMessage);
            return ExitCodes.Error;
        }

        if (!autostart.IsEnabled)
        {
            AnsiConsole.MarkupLine("[dim]Autostart is not enabled — nothing to unregister.[/]");
            return ExitCodes.Ok;
        }

        DaemonAutostartDisableOutcome outcome = await autostart.DisableAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]Autostart disabled[/]: the LaunchAgent is unregistered.");
        AnsiConsole.MarkupLine(outcome switch
        {
            DaemonAutostartDisableOutcome.DaemonStopped =>
                "[yellow]The launchd-owned daemon was stopped with it[/] — start it on demand again with h9k daemon start.",
            DaemonAutostartDisableOutcome.DaemonStopping =>
                "[yellow]The launchd-owned daemon was signalled and is still shutting down[/] — it finishes in-flight "
                + "event appends first; h9k daemon status says when it is gone.",
            _ => "[dim]No running daemon was stopped; whatever runs now is untouched and starts only when "
                + "h9k daemon start runs it.[/]",
        });
        return ExitCodes.Ok;
    }
}
