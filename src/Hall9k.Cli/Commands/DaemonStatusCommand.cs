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
        DaemonBootStatus bootStatus = DaemonProcess.ProbeBootStatus();
        switch (bootStatus)
        {
            case { State: DaemonBootState.Running, Running: { } running }:
                AnsiConsole.MarkupLineInterpolated(
                    $"[green]h9kd: running[/] (pid {running.ProcessId}) — up {Uptime(DateTimeOffset.UtcNow - running.StartedAt)}, started {running.StartedAt:u}");
                break;
            case { State: DaemonBootState.Starting }:
                AnsiConsole.MarkupLine(
                    "[yellow]h9kd: starting[/] — a marker recorded moments ago says a spawn is in flight, "
                    + "but whether it is still booting (assembly resolution and JIT for the entry point, "
                    + "before it ever reaches its own single-instance guard, has taken up to ~15s on at "
                    + "least one real machine) or already died before writing its pid file isn't known "
                    + "here; a second h9k daemon start here is refused rather than risked against it. "
                    + "Check again shortly.");
                break;
            default:
                AnsiConsole.MarkupLine(
                    "[red]h9kd: not running[/] — tasks queue but do not dispatch; start it with [bold]h9k daemon start[/]");
                break;
        }

        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
        AnsiConsole.MarkupLine(autostart switch
        {
            { IsSupported: false } => $"[dim]autostart: {autostart.MechanismDescription}[/]",
            { IsEnabled: true } => $"[dim]autostart: enabled ({autostart.MechanismDescription} — starts at login; disable with h9k daemon autostart disable)[/]",
            _ => "[dim]autostart: disabled (opt in with h9k daemon autostart enable)[/]",
        });

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cancellationToken);
        foreach (string line in OperatingSettingsRendering.ProblemLines(report))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{line}[/]");
        }

        string settingsHeader = bootStatus switch
        {
            { State: DaemonBootState.Running, Running: { } running } =>
                $"settings a daemon started now would resolve, not observed from pid {running.ProcessId} itself — a "
                + "running daemon binds configuration once, at startup, so none of these origins are guaranteed to "
                + "match what it actually started with: an (env: …) origin may not match because an autostarted "
                + "daemon never receives Hall9k__ variables at all, and a (config: …) origin or built-in default may "
                + "not match either if the file was created, edited, or removed after this daemon started (h9k "
                + "config show for the full picture, h9k config set to change one):",
            { State: DaemonBootState.Starting } =>
                "settings a daemon started now would resolve — the daemon still booting above will bind its own "
                + "configuration once, at startup, once it is up (h9k config show for the full picture, h9k config "
                + "set to change one):",
            _ => "settings a daemon started now would resolve (h9k config show for the full picture, h9k config set to change one):",
        };
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
