using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>One glance at the daemon itself: running or not, pid, uptime, autostart posture, and the log tail.</summary>
public sealed class DaemonStatusCommand : Hall9kAsyncCommand<DaemonStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    private const int TailLines = 8;

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
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

        IReadOnlyList<string> tail = DaemonLog.Tail(TailLines);
        AnsiConsole.MarkupLineInterpolated($"[dim]log: {DaemonRuntime.LogFile}[/]");
        foreach (string line in tail)
        {
            AnsiConsole.MarkupLineInterpolated($"  [dim]{line}[/]");
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    private static string Uptime(TimeSpan uptime) => uptime switch
    {
        { TotalMinutes: < 1 } => $"{(int)uptime.TotalSeconds}s",
        { TotalHours: < 1 } => $"{(int)uptime.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
        _ => $"{(int)uptime.TotalDays}d {uptime.Hours}h",
    };
}
