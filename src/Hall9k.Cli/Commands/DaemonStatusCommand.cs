using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>One glance at the daemon itself: running or not, pid, uptime, autostart posture, and the log tail.</summary>
public sealed class DaemonStatusCommand : Hall9kAsyncCommand<DaemonStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    private const int TailLines = 8;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        DaemonProcessDescriptor? running = DaemonProcess.Probe();
        if (running is null)
        {
            AnsiConsole.MarkupLine(
                "[red]h9kd: not running[/] — tasks queue but do not dispatch; start it with [bold]h9k daemon start[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[green]h9kd: running[/] (pid {running.ProcessId}) — up {Uptime(DateTimeOffset.UtcNow - running.StartedAt)}, started {running.StartedAt:u}");
        }

        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
        AnsiConsole.MarkupLine(autostart switch
        {
            { IsSupported: false } => "[dim]autostart: not available on this platform yet[/]",
            { IsEnabled: true } => "[dim]autostart: enabled (launchd LaunchAgent — starts at login; disable with h9k daemon autostart disable)[/]",
            _ => "[dim]autostart: disabled (opt in with h9k daemon autostart enable)[/]",
        });

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cancellationToken);
        foreach (string line in OperatingSettingsRendering.ProblemLines(report))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{line}[/]");
        }

        string settingsHeader = running is null
            ? "settings a daemon started now would resolve (h9k config show for the full picture, h9k config set to change one):"
            : $"settings a daemon started now would resolve, not observed from pid {running.ProcessId} itself — a "
              + "running daemon binds configuration once, at startup, so none of these origins are guaranteed to "
              + "match what it actually started with: an (env: …) origin may not match because an autostarted "
              + "daemon never receives Hall9k__ variables at all, and a (config: …) origin or built-in default may "
              + "not match either if the file was created, edited, or removed after this daemon started (h9k "
              + "config show for the full picture, h9k config set to change one):";
        AnsiConsole.MarkupLineInterpolated($"[dim]{settingsHeader}[/]");
        foreach ((string label, string value) in OperatingSettingsRendering.Rows(report))
        {
            AnsiConsole.MarkupLineInterpolated($"  [dim]{label}: {value}[/]");
        }

        IReadOnlyList<string> tail = DaemonLog.Tail(TailLines);
        AnsiConsole.MarkupLineInterpolated($"[dim]log: {DaemonRuntime.LogFile}[/]");
        foreach (string line in tail)
        {
            AnsiConsole.MarkupLineInterpolated($"  [dim]{line}[/]");
        }

        return ExitCodes.Ok;
    }

    private static string Uptime(TimeSpan uptime) => uptime switch
    {
        { TotalMinutes: < 1 } => $"{(int)uptime.TotalSeconds}s",
        { TotalHours: < 1 } => $"{(int)uptime.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
        _ => $"{(int)uptime.TotalDays}d {uptime.Hours}h",
    };
}
